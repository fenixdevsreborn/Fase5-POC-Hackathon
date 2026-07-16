# Runbook Operacional - Conexao Solidaria

Procedimentos para diagnosticar e recuperar os incidentes mais comuns da
plataforma. Todos os nomes (filas, tabelas, metricas, deployments) sao os reais
do projeto.

## Referencia rapida

| Recurso            | Nome real |
|--------------------|-----------|
| Namespace k8s      | `conexao-solidaria` |
| Deployments        | `identity-api`, `campaigns-api`, `donations-worker`, `gateway`, `web`, `postgres`, `rabbitmq`, `elasticsearch` |
| Jobs de migracao   | `identity-migrations`, `campaigns-migrations` (`RunMigrationsOnly=true`) |
| Fila principal     | `doacoes-recebidas` |
| Retry queues       | `doacoes.retry.10s`, `doacoes.retry.60s` (TTL 10s / 60s) |
| Dead-letter queue  | `doacoes.dead-letter` |
| Fila de notificacoes | anonima/exclusiva por replica do Web, ligada ao fanout `conexao-solidaria.notifications` |
| Exchanges          | `conexao-solidaria` (direct, principal), `conexao-solidaria.retry`, `conexao-solidaria.dlx`, `conexao-solidaria.notifications` (fanout) |
| Routing key        | `doacao.recebida` |
| Bancos (Postgres)  | `identitydb`, `campaignsdb`, `zabbixdb` |
| Tabela outbox      | `outbox_messages` (em `campaignsdb`) |
| Tabelas doacao     | `donations`, `campaigns`, `processed_messages`, `donation_idempotency_keys` |
| Read model         | `campaign_stats` (em `campaignsdb`) |
| Health endpoint    | `/health` (porta interna 8080) |
| Metrics endpoint   | `/metrics` (prometheus-net) |
| Endpoint publico   | `GET /api/campanhas/transparencia` |

### Metricas de negocio (Prometheus)

| Metrica | Servico | Significado |
|---------|---------|-------------|
| `conexao_outbox_pending_messages` (gauge)              | campaigns-api | mensagens pendentes na outbox |
| `conexao_donation_publish_total` (counter)             | campaigns-api | eventos publicados com sucesso |
| `conexao_donation_publish_failures_total` (counter)    | campaigns-api | falhas ao publicar no broker |
| `conexao_donations_processed_total` (counter)          | donations-worker | doacoes processadas |
| `conexao_donations_rejected_total` (counter)           | worker + campaigns-api | doacoes rejeitadas (campanha encerrada/cancelada) |
| `conexao_dead_letter_messages` (counter)               | donations-worker | mensagens enviadas a DLQ |
| `conexao_donation_processing_duration_seconds` (histogram) | donations-worker | duracao do processamento |

### Acessos rapidos

| Ferramenta          | compose                     | k8s (NodePort) |
|---------------------|-----------------------------|----------------|
| RabbitMQ Management | http://localhost:15672      | http://<node>:31672 |
| Prometheus          | http://localhost:9090       | http://<node>:30090 |
| Grafana             | http://localhost:3000       | http://<node>:30300 |
| Zabbix              | http://localhost:8085       | http://<node>:30085 |

Credenciais: RabbitMQ = `RABBITMQ_USER`/`RABBITMQ_PASSWORD`; Grafana =
`GRAFANA_ADMIN_USER`/`GRAFANA_ADMIN_PASSWORD` (do `.env` / Secret).

---

## Comandos uteis

### Kubernetes

```bash
NS=conexao-solidaria

# Estado geral
kubectl get pods -n $NS -o wide
kubectl get deploy -n $NS

# Logs (seguir)
kubectl logs -n $NS deploy/donations-worker -f
kubectl logs -n $NS deploy/campaigns-api -f --since=10m

# Diagnostico de um pod que nao sobe
kubectl describe pod -n $NS -l app=donations-worker
kubectl get events -n $NS --sort-by=.lastTimestamp | tail -30

# Escalar (usado no cenario de falha/recuperacao)
kubectl scale deployment donations-worker --replicas=0 -n $NS
kubectl scale deployment donations-worker --replicas=1 -n $NS

# Reiniciar rollout
kubectl rollout restart deploy/donations-worker -n $NS
kubectl rollout status  deploy/donations-worker -n $NS
```

### RabbitMQ (Management HTTP API)

O vhost padrao `/` e codificado como `%2f` na URL.

```bash
# Profundidade das filas (campo .messages)
curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" \
  http://localhost:15672/api/queues/%2f/doacoes-recebidas | jq '{messages, consumers}'

curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" \
  http://localhost:15672/api/queues/%2f/doacoes.dead-letter | jq '.messages'

# Todas as filas de uma vez
curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" \
  http://localhost:15672/api/queues/%2f | jq '.[] | {name, messages, consumers}'
```

Dentro do pod/container: `rabbitmqctl list_queues name messages consumers`.

### PostgreSQL (campaignsdb)

```bash
# Abrir psql no pod do postgres
kubectl exec -it -n conexao-solidaria deploy/postgres -- psql -U postgres -d campaignsdb
```

```sql
-- Outbox pendente (ainda nao publicada)
SELECT count(*) FROM outbox_messages WHERE "PublishedAtUtc" IS NULL;

-- Detalhe do backlog de outbox: tentativas e ultimo erro
SELECT "Id", "EventType", "Attempts", "NextAttemptAtUtc", "LastError", "OccurredAtUtc"
FROM outbox_messages
WHERE "PublishedAtUtc" IS NULL
ORDER BY "OccurredAtUtc"
LIMIT 20;

-- Doacoes ainda nao processadas pelo worker (sem data de processamento)
SELECT count(*) FROM donations WHERE "ProcessadaEm" IS NULL;

-- Total arrecadado por campanha
SELECT "Id", "Titulo", "Status", "MetaFinanceira", "ValorTotalArrecadado"
FROM campaigns ORDER BY "ValorTotalArrecadado" DESC;

-- Idempotencia: mensagens ja processadas (dedup do worker)
SELECT count(*) FROM processed_messages;
```

---

## Cenario 1 - Fila `doacoes-recebidas` acumulando (backlog)

**Sintomas:** trigger Zabbix "Fila doacoes-recebidas acima de 20 por 2min";
`.messages` crescente na Management API; painel de outbox/pendentes no Grafana.

**Diagnostico:**
1. O worker esta vivo e consumindo?
   ```bash
   kubectl get pods -n conexao-solidaria -l app=donations-worker
   curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" \
     http://localhost:15672/api/queues/%2f/doacoes-recebidas | jq '.consumers'
   ```
   `consumers = 0` => nenhum worker conectado.
2. Logs do worker por excecao/loop de erro:
   `kubectl logs -n conexao-solidaria deploy/donations-worker --tail=100`
3. Banco disponivel? O worker atualiza `campaigns.ValorTotalArrecadado`; se o
   Postgres estiver fora, o processamento trava (ver Cenario 5).

**Recuperacao:**
- Worker parado/0 replicas: `kubectl scale deployment donations-worker --replicas=1 -n conexao-solidaria`.
- Worker em CrashLoop: `kubectl describe pod` + logs; corrigir causa (conexao
  RabbitMQ/Postgres, env). Depois `kubectl rollout restart deploy/donations-worker`.
- Backlog grande porem worker saudavel: aguardar a drenagem; acompanhar
  `.messages` cair e `conexao_donations_processed_total` subir.

---

## Cenario 2 - DLQ `doacoes.dead-letter` com mensagens (> 0)

**Sintomas:** trigger Zabbix "DLQ doacoes.dead-letter com mensagens (>0)";
`conexao_dead_letter_messages` incrementando.

**Contexto:** o worker tenta processar; em falha faz retry via
`doacoes.retry.10s` -> `doacoes.retry.60s`. Apos `MaxAttempts = 3`, a mensagem
vai para `doacoes.dead-letter` (header `x-attempts` acompanha as tentativas).

**Diagnostico:**
1. Inspecionar as mensagens sem consumir, pela UI: RabbitMQ Management ->
   Queues -> `doacoes.dead-letter` -> Get messages (Ack mode: *Reject / requeue
   true* para nao remover).
2. Ver o payload (evento `DoacaoRecebidaEvent`) e o `LastError` correlato na
   `outbox_messages` / logs do worker para a causa raiz (ex.: campanha
   inexistente, dado invalido, bug de desserializacao).

**Recuperacao:**
1. Corrigir a causa raiz (deploy de fix, ajuste de dados).
2. Reprocessar: RabbitMQ Management -> `doacoes.dead-letter` -> **Move messages**
   para a exchange `conexao-solidaria` com routing key `doacao.recebida`
   (via plugin shovel) ou Get + re-publish. Alternativamente, republique o
   evento a partir do payload.
3. Confirmar drenagem da DLQ (`.messages` = 0) e `conexao_donations_processed_total`
   subindo.

---

## Cenario 3 - Worker parado (`donations-worker`)

**Sintomas:** trigger Zabbix "Donations Worker indisponivel por 2min"; fila
subindo; `/health` do worker sem 200.

**Diagnostico:**
```bash
kubectl get pods -n conexao-solidaria -l app=donations-worker
kubectl describe pod -n conexao-solidaria -l app=donations-worker   # Events/estado
kubectl logs -n conexao-solidaria deploy/donations-worker --tail=100
```
Causas comuns: replicas em 0 (scale manual/demo), CrashLoopBackOff (falha de
conexao RabbitMQ/Postgres, env ausente), readiness falhando.

**Recuperacao:**
```bash
kubectl scale deployment donations-worker --replicas=1 -n conexao-solidaria
kubectl rollout status deploy/donations-worker -n conexao-solidaria
```
Validar: `/health` -> 200; `consumers >= 1` na fila; backlog drenando.

---

## Cenario 4 - Falhas de publicacao (outbox nao drena)

**Sintomas:** `conexao_donation_publish_failures_total` subindo;
`conexao_outbox_pending_messages` alto e estavel; doacoes aceitas (HTTP 202) mas
sem chegar na fila.

**Contexto:** ao aceitar uma doacao, a Campaigns.Api grava `donations` +
`outbox_messages` atomicamente (padrao Outbox). O `OutboxDispatcherWorker`
(dentro da Campaigns.Api) varre a cada 1s em lotes de 20 e publica no RabbitMQ.
Se o broker estiver indisponivel, a mensagem fica pendente com `Attempts`
incrementando e `NextAttemptAtUtc` no futuro.

**Diagnostico:**
```bash
kubectl logs -n conexao-solidaria deploy/campaigns-api --tail=100 | grep -i outbox
```
```sql
SELECT count(*) FROM outbox_messages WHERE "PublishedAtUtc" IS NULL;
SELECT "Attempts", "LastError", "NextAttemptAtUtc"
FROM outbox_messages WHERE "PublishedAtUtc" IS NULL
ORDER BY "Attempts" DESC LIMIT 10;
```
- RabbitMQ no ar? `kubectl get pods -n conexao-solidaria -l app=rabbitmq` e
  Management API respondendo.

**Recuperacao:**
- Restabelecer o RabbitMQ (ver saude do pod, reiniciar se necessario). O
  dispatcher **retoma sozinho** e publica a outbox pendente (nao ha perda: as
  mensagens estao persistidas). Acompanhar `conexao_outbox_pending_messages` cair
  e `conexao_donation_publish_total` subir.

---

## Cenario 5 - Banco de dados indisponivel (`postgres`)

**Sintomas:** `/health` das APIs falhando; erros de conexao nos logs de
identity-api / campaigns-api / donations-worker; escritas e processamento parados.

**Diagnostico:**
```bash
kubectl get pods -n conexao-solidaria -l app=postgres
kubectl describe pod -n conexao-solidaria -l app=postgres
kubectl logs -n conexao-solidaria deploy/postgres --tail=100
# readiness usa pg_isready
kubectl exec -n conexao-solidaria deploy/postgres -- pg_isready -U postgres
```

**Recuperacao:**
1. Recuperar o Postgres (pod/volume). Bancos: `identitydb`, `campaignsdb`,
   `zabbixdb` (o script `infra/postgres/init/01-create-databases.sh` cria os
   bancos e o usuario `zabbix` no primeiro boot).
2. Com o banco de volta, as APIs voltam ao `/health` 200. Nenhuma doacao aceita
   e perdida: o que estava na outbox continua persistido e sera publicado; o que
   estava na fila sera consumido. `processed_messages` garante idempotencia (sem
   duplo credito no total da campanha).

---

## Cenario 6 - Migracao / schema nao aplicado (Jobs + NetworkPolicy)

**Sintomas:** apos `kubectl apply -k`, `identity-api` / `campaigns-api` /
`donations-worker` em **CrashLoopBackOff** ou presos no boot; logs com
"Schema do banco ainda nao pronto" ou erros de tabela inexistente
(`relation "donations" does not exist`); Jobs de migracao em `0/1` ou sem `Complete`.

**Contexto:** o schema e aplicado por **Jobs** (`identity-migrations`,
`campaigns-migrations`) com `RunMigrationsOnly=true`, **nao** pelos apps no boot
(deployments sobem com `Migrations__RunOnStartup=false`). Os apps apenas **aguardam**
o schema num loop curto de **10 tentativas x 3s (~30s)**; se esgotar, entram em
CrashLoop. Ver `ReadmeKubernetes.md` secao 6-7.

**Diagnostico:**
```bash
NS=conexao-solidaria
kubectl get jobs -n $NS
kubectl logs -n $NS job/campaigns-migrations
kubectl logs -n $NS job/identity-migrations
# App preso esperando schema:
kubectl logs -n $NS deploy/donations-worker --tail=50 | grep -i schema
```
Duas causas frequentes:
1. **NetworkPolicy bloqueando o Job → Postgres.** Com `default-deny-ingress`, o
   Postgres so aceita a allow-list. Os Jobs usam labels `app=identity-migrations` /
   `app=campaigns-migrations` (diferentes dos deployments); a policy `allow-to-postgres`
   **precisa** liberar esses labels na porta 5432 (ja corrigido no repo). Verifique:
   ```bash
   kubectl describe networkpolicy allow-to-postgres -n $NS
   ```
   Se o log do Job mostra timeout de conexao ao `postgres:5432`, e a policy.
2. **Timing:** Postgres ainda inicializando o volume quando o app esgotou os ~30s.

**Recuperacao:**
```bash
# Garantir migracoes concluidas primeiro
kubectl wait --for=condition=complete --timeout=180s \
  job/identity-migrations job/campaigns-migrations -n $NS
# Reiniciar os apps para reentrarem no loop com o schema pronto
kubectl rollout restart deploy/identity-api deploy/campaigns-api deploy/donations-worker -n $NS
kubectl rollout status  deploy/campaigns-api -n $NS
```
Se o Job falhou por schema legado (volume criado por `EnsureCreated`, sem
`__EFMigrationsHistory`): recriar o PVC do Postgres
(`kubectl delete pvc -n $NS -l app=postgres`) e reaplicar. Ver AD-11.
**Recomendacao (producao):** initContainer que bloqueia o start ate a migracao concluir.

---

## Cenario 7 - Read model `campaign_stats` divergente

**Sintomas:** `GET /api/campanhas/stats` (ou `GET /api/campanhas/transparencia`) mostra
total/contagem que **nao batem** com as doacoes reais; painel de transparencia
"atrasado" em relacao ao valor da campanha.

**Contexto:** o Worker, na **mesma transacao** em que credita
`campaigns.ValorTotalArrecadado`, faz **upsert** do read model `campaign_stats`
(CampaignId, Titulo, MetaFinanceira, TotalArrecadado, DoacoesProcessadas, AtualizadoEm).
Se o processamento parou (Worker caido / backlog) ou uma correcao de dados foi feita
direto em `donations`/`campaigns` sem passar pelo Worker, o read model fica defasado.

**Diagnostico:** comparar a fonte da verdade com o read model.
```bash
kubectl exec -it -n conexao-solidaria deploy/postgres -- psql -U postgres -d campaignsdb
```
```sql
-- Fonte da verdade (agregada das doacoes processadas) x read model
SELECT c."Id", c."Titulo", c."ValorTotalArrecadado" AS total_campanha,
       s."TotalArrecadado" AS total_stats,
       s."DoacoesProcessadas" AS stats_qtd,
       (SELECT count(*) FROM donations d
         WHERE d."CampaignId" = c."Id" AND d."ProcessadaEm" IS NOT NULL) AS doacoes_ok,
       s."AtualizadoEm"
FROM campaigns c
LEFT JOIN campaign_stats s ON s."CampaignId" = c."Id"
ORDER BY c."Id";
```
Divergencia entre `total_campanha` e `total_stats`, ou entre `stats_qtd` e `doacoes_ok`,
confirma o problema.

**Recuperacao:**
1. Primeiro garantir que o Worker esta drenando (Cenarios 1/3) e a fila zerada - o
   read model se auto-corrige conforme as mensagens sao processadas.
2. Se ha divergencia residual (correcao manual no passado), reconciliar o read model a
   partir da fonte da verdade:
   ```sql
   UPDATE campaign_stats s
   SET "TotalArrecadado" = c."ValorTotalArrecadado",
       "DoacoesProcessadas" = sub.qtd,
       "AtualizadoEm" = now()
   FROM campaigns c
   JOIN (
     SELECT "CampaignId", count(*) AS qtd
     FROM donations WHERE "ProcessadaEm" IS NOT NULL
     GROUP BY "CampaignId"
   ) sub ON sub."CampaignId" = c."Id"
   WHERE s."CampaignId" = c."Id";
   ```
3. Validar via `GET /api/campanhas/stats` (read model) e `GET /api/campanhas/{id}`.
   Lembre do output cache de 5s na transparencia (`/api/campanhas/transparencia`).

---

## Cenario 8 - Consumer de notificacoes do Web caido (fallback = polling)

**Sintomas:** doacoes sao processadas normalmente (dashboards e totais corretos), mas a
UI do doador/gestor **nao atualiza em tempo real** - o valor so muda ao recarregar a
pagina; a fila anonima ligada ao fanout `conexao-solidaria.notifications` sem consumer.

**Contexto:** ao processar com sucesso, o Worker publica
`DoacaoProcessadaNotification` no **fanout** `conexao-solidaria.notifications`
(best-effort, so no sucesso). O Web roda um `NotificationConsumer` (BackgroundService
resiliente) que le uma fila anonima/exclusiva desse fanout e empurra a atualizacao para a
UI via **SignalR** (`NotificationDispatcher`). Se esse consumer cai (RabbitMQ
indisponivel na hora, restart do Web, NetworkPolicy Web->RabbitMQ), o tempo real para -
**mas nada e perdido no negocio**: a doacao ja foi processada e persistida; o fallback e
recarregar/**polling** da UI, que le o estado atual das APIs.

**Diagnostico:**
```bash
NS=conexao-solidaria
kubectl get pods -n $NS -l app=web
kubectl logs -n $NS deploy/web --tail=100 | grep -iE "notification|rabbit|signalr"
# Existe consumer no fanout? (a fila anonima aparece ligada ao exchange)
curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" \
  http://localhost:15672/api/exchanges/%2f/conexao-solidaria.notifications/bindings/source \
  | jq '.[] | {destination, routing_key}'
```
Causas comuns: Web em restart/CrashLoop; RabbitMQ fora (Cenario 4/5 correlato);
NetworkPolicy `allow-to-rabbitmq` sem o label `app=web` (ja liberado no repo - o Web
consome o fanout).

**Recuperacao:**
- Web parado/CrashLoop: `kubectl rollout restart deploy/web -n $NS` e
  `kubectl rollout status deploy/web -n $NS`. O consumer reconecta sozinho (BackgroundService
  resiliente) e recria a fila anonima no fanout.
- RabbitMQ fora: restabelecer o broker (Cenario 4/5); o consumer do Web se reconecta.
- Confirmar `app=web` na policy `allow-to-rabbitmq` (porta 5672) se o log mostrar
  bloqueio de conexao.
- Enquanto isso, a experiencia degrada com elegancia: a UI continua correta via
  recarregar/polling; nao ha reprocessamento nem duplicidade (idempotencia por EventId).

---

## Cenario 9 - Busca pobre / indice do Elasticsearch ausente ou com mapeamento antigo

**Sintomas:** `GET /api/campanhas/search?q=...` responde 200, mas a busca "emburrece": um termo
com typo ou **sem acento** nao acha nada (`sao paulo` nao retorna "Sao Paulo"), categoria nao e
pesquisavel, ou campanhas que existem no Postgres nao aparecem. Nos logs da `campaigns-api`:
"Busca no Elasticsearch indisponivel ... fallback para PostgreSQL" ou "Circuito do Elasticsearch
ABERTO".

**Contexto:** a busca usa o indice `campanhas` com analisadores pt-BR (AD-35). O indice e criado
no startup por `EnsureIndexAsync` **somente se nao existir**; quando e criado, a API faz
**backfill** de todas as campanhas do Postgres (fonte da verdade). Dois modos de falha distintos:

- **ES fora** -> a busca degrada para o Postgres com ILIKE (AD-12/AD-34): resultados corretos,
  porem sem fuzzy/acento/categoria. Nao ha nada a fazer no indice.
- **Indice com mapeamento antigo** -> o ES **nao permite trocar o analisador** de um campo ja
  criado, e o `EnsureIndexAsync` nao altera indice existente. Um indice criado por mapeamento
  dinamico (analisador `standard`) **continua velho mesmo apos o deploy do codigo novo** - o
  sintoma e busca sem fuzzy/acento com o ES saudavel e sem erro nos logs.

**Diagnostico:**
```bash
NS=conexao-solidaria
ES=$(kubectl get pod -n $NS -l app=elasticsearch -o jsonpath='{.items[0].metadata.name}')
# 1. O indice existe e tem documentos?
kubectl exec -n $NS $ES -- curl -s "http://localhost:9200/_cat/indices/campanhas?v"
# 2. O mapeamento e o novo? titulo DEVE ter "analyzer":"campanha_analyzer" e existir categoriaTexto
kubectl exec -n $NS $ES -- curl -s "http://localhost:9200/campanhas/_mapping"
# 3. O startup criou o indice e fez o backfill?
kubectl logs -n $NS deploy/campaigns-api | grep -iE "indice|backfill"
# 4. O ES esta de pe?
kubectl get pods -n $NS -l app=elasticsearch
```
Se (2) nao mostrar `campanha_analyzer`, o indice esta com o mapeamento antigo.

**Recuperacao (indice antigo/defasado) - dropar e deixar a API recriar + repopular:**
```bash
kubectl exec -n $NS $ES -- curl -s -X DELETE "http://localhost:9200/campanhas"
kubectl rollout restart deploy/campaigns-api -n $NS
kubectl rollout status  deploy/campaigns-api -n $NS --timeout=180s
kubectl logs -n $NS deploy/campaigns-api | grep -iE "indice|backfill"
# esperado:
#   Indice 'campanhas' criado no Elasticsearch com analisadores pt-BR.
#   Indice recem-criado: iniciando backfill de N campanha(s).
#   Backfill do Elasticsearch indexou N campanha(s).
```
**Seguro por design:** o Postgres e a fonte da verdade e o backfill reindexa tudo. Enquanto o
indice nao existe, a busca cai no fallback do Postgres - o usuario nao ve erro, so a busca
degradada por alguns segundos.

- **ES fora:** restabelecer o pod `elasticsearch`; a busca segue no fallback ate o circuito fechar
  sozinho (half-open, AD-34). Nenhuma acao no indice e necessaria.
- **Deploy que muda mapeamento/analisador:** exige o mesmo drop + restart acima - `EnsureIndexAsync`
  nao migra indice existente (AD-35).
- **Atencao ao rebuildar a imagem:** o deployment `campaigns-api` usa a tag **`catv1`** (ver
  `overlays/local/kustomization.yaml`), nao `local`. Buildar/importar so a `:local` reinicia o pod
  com a imagem **antiga** e o codigo novo nao entra.

**Verificacao (com `kubectl port-forward -n $NS svc/gateway 18080:80`):**
```bash
# sem acento e com typo devem retornar resultado
curl -s "http://localhost:18080/api/campanhas/search?q=sao%20paulo&pageSize=3"
curl -s "http://localhost:18080/api/campanhas/search?q=cadera%20gamer&pageSize=3"
```

---

## Boas praticas de verificacao pos-incidente

- Zabbix -> Monitoring -> Problems: alertas resolvidos (recovery).
- Grafana: `conexao_outbox_pending_messages` ~ 0, `conexao_donations_processed_total`
  voltando a crescer, filas em 0.
- `GET /api/campanhas/transparencia` retorna 200 e totais coerentes.
