# Cenario de Falha e Recuperacao - Roteiro da Demo

Demonstracao ao vivo da resiliencia da Conexao Solidaria: derrubamos o
`donations-worker`, geramos carga de doacoes, observamos o backlog subir e os
alertas dispararem, e entao restauramos o worker para ver a fila drenar, o total
da campanha atualizar e os alertas se recuperarem sozinhos.

**Duracao:** ~6-8 min. **Mensagem-chave:** nenhuma doacao e perdida - o padrao
Outbox + filas com retry/DLQ + idempotencia garantem entrega e consistencia.

---

## Pre-requisitos

- Stack no ar (k8s namespace `conexao-solidaria`, ou docker-compose).
- Zabbix com o template `Conexao Solidaria` importado e host linkado
  (ver `infra/zabbix/README.md`).
- Abas abertas: **Grafana** (dashboard Conexao Solidaria), **Zabbix**
  (Monitoring -> Problems), **RabbitMQ Management** (Queues), e um terminal.
- Uma campanha `Ativa` existente e um token de doador (ver `scripts/demo.http`).

```bash
export NS=conexao-solidaria
export CAMPAIGNS=http://localhost:5002          # compose; k8s: http://localhost:18082 (port-forward)
export RMQ=http://localhost:15672               # compose; k8s: http://localhost:15672 (port-forward)
# No k8s, o `up.ps1` ja deixa esses port-forwards ativos (campaigns-api 18082, rabbitmq 15672).
```

Estado saudavel esperado no inicio: fila `doacoes-recebidas` com `consumers >= 1`
e `messages = 0`; sem problemas ativos no Zabbix.

---

## Passo 1 - Provocar a falha: derrubar o worker

```bash
kubectl scale deployment donations-worker --replicas=0 -n $NS
kubectl get pods -n $NS -l app=donations-worker      # deve zerar
```

**Observar:**
- RabbitMQ Management -> `doacoes-recebidas`: `consumers` cai para `0`.
- Zabbix (apos ~2 min): trigger **"Donations Worker indisponivel por 2min"**
  (HIGH) - o `/health` do worker para de responder.

> docker-compose: `docker compose stop donations-worker`.

---

## Passo 2 - Gerar doacoes (a fila comeca a encher)

Envie varias doacoes para a campanha ativa (repita 25+ vezes para cruzar o
limite de 20). Exemplo com o fluxo do `scripts/demo.http`:

```bash
# (login do doador para obter o token - ver scripts/demo.http)
for i in $(seq 1 30); do
  curl -s -X POST "$CAMPAIGNS/api/doacoes" \
    -H "Authorization: Bearer $DOADOR_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{\"idCampanha\":\"$CAMPANHA_ID\",\"valorDoacao\":50}" \
    -o /dev/null -w "%{http_code}\n"
done
```

Cada POST retorna **202 Accepted**: a doacao e gravada em `donations` +
`outbox_messages` atomicamente. O `OutboxDispatcherWorker` (na Campaigns.Api)
publica na fila `doacoes-recebidas` - mas **ninguem consome**, entao a fila cresce.

**Observar:**
- RabbitMQ: `doacoes-recebidas` -> `messages` subindo (30).
- Grafana: `conexao_donation_publish_total` sobe (publicacao OK), mas o total da
  campanha (`ValorTotalArrecadado`) **NAO** muda ainda - o worker e quem credita.
  Painel de doacoes processadas (`conexao_donations_processed_total`) estavel.
- Verificacao no banco (doacoes aceitas, ainda nao processadas):
  ```sql
  SELECT count(*) FROM donations WHERE "ProcessadaEm" IS NULL;
  ```

---

## Passo 3 - Alertas disparam no Zabbix

Apos ~2 minutos com a fila acima de 20:

- Zabbix -> Monitoring -> Problems:
  - **"Fila doacoes-recebidas acima de 20 por 2min"** (HIGH).
  - **"Donations Worker indisponivel por 2min"** (HIGH) - do Passo 1.
- Narrativa: o monitoramento detectou o incidente **sem intervencao manual**; um
  operador seria notificado agora.

Confirmacao por API:
```bash
curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" \
  "$RMQ/api/queues/%2f/doacoes-recebidas" | jq '{messages, consumers}'
```

---

## Passo 4 - Recuperar: restaurar o worker

```bash
kubectl scale deployment donations-worker --replicas=1 -n $NS
kubectl rollout status deploy/donations-worker -n $NS
```

> docker-compose: `docker compose start donations-worker`.

O worker sobe, reconecta ao RabbitMQ e comeca a consumir a fila imediatamente.

---

## Passo 5 - Observar a drenagem e a recuperacao

**RabbitMQ:** `consumers` volta a `>= 1`; `messages` de `doacoes-recebidas` cai
rapidamente ate `0`.

**Grafana:**
- `conexao_donations_processed_total` sobe em rajada (as 30 doacoes).
- `ValorTotalArrecadado` da campanha aumenta (30 x R$50 = R$1.500) e o painel de
  transparencia/total se atualiza.
- `conexao_outbox_pending_messages` ~ 0.

**Banco:**
```sql
SELECT count(*) FROM donations WHERE "ProcessadaEm" IS NULL;   -- volta a 0
SELECT "Titulo", "ValorTotalArrecadado" FROM campaigns WHERE "Id" = '<CAMPANHA_ID>';
```

**Endpoint publico:**
```bash
curl -s "$CAMPAIGNS/api/campanhas/transparencia" | jq
```
O total reflete todas as doacoes - **nada foi perdido** durante a queda.

**Zabbix:** as triggers "Fila ... acima de 20" e "Donations Worker indisponivel"
entram em estado **Resolved/OK** automaticamente quando fila e `/health`
normalizam.

---

## Passo 6 (opcional) - Idempotencia sob reentrega

Para reforcar a robustez: uma reentrega da mesma mensagem (mesmo `EventId`) NAO
credita o total duas vezes - o worker registra em `processed_messages` e ignora
duplicatas. O total permanece correto.

---

## O que observar em cada ferramenta (resumo)

| Momento           | RabbitMQ            | Grafana                                   | Zabbix                        | Banco (`campaignsdb`) |
|-------------------|---------------------|-------------------------------------------|-------------------------------|-----------------------|
| Worker derrubado  | consumers = 0       | processados estaveis                      | Worker indisponivel (HIGH)    | -                     |
| Doacoes enviadas  | messages sobe (30)  | publish_total sobe; total inalterado      | -                             | `ProcessadaEm IS NULL` cresce |
| Apos ~2 min       | messages > 20       | outbox/backlog visivel                    | Fila > 20 (HIGH)              | -                     |
| Worker restaurado | consumers >= 1      | processados sobem; total atualiza         | -                             | total incrementa      |
| Estabilizado      | messages = 0        | outbox ~ 0                                | alertas em Resolved/OK        | `ProcessadaEm IS NULL` = 0 |

## Comandos de reset (deixar limpo para reexecutar)

```bash
# Garantir o worker no ar
kubectl scale deployment donations-worker --replicas=1 -n conexao-solidaria
# Conferir filas zeradas
curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" "$RMQ/api/queues/%2f" \
  | jq '.[] | select(.name|startswith("doacoes")) | {name, messages}'
```
