# Observabilidade - Conexão Solidária

Como rodar, acessar e interpretar a observabilidade do projeto. A stack cobre três
camadas:

- **Métricas de negócio, aplicação e mensageria** → `prometheus-net` (`/metrics`) →
  **Prometheus** → **Grafana** (3 dashboards + alertas).
- **Tracing distribuído (traces/métricas/logs)** → **OpenTelemetry** (OTLP) →
  **Aspire Dashboard** (dev) ou **OTel Collector** → **Tempo** (traces) + Prometheus
  (métricas), explorados na aba **Explore** do Grafana (datasource Tempo).
- **Disponibilidade / profundidade de fila** → **Zabbix** (template real).

Decisões e trade-offs em `docs/decisoes-arquiteturais.md` (AD-20, AD-21). Operação de
incidentes em `docs/runbook.md`. Demonstração de queda e recuperação em
`docs/cenario-falha-recuperacao.md`.

## Instrumentação dos serviços

Todos os serviços `.NET 10` expõem, via `ServiceDefaults` (`MapDefaultEndpoints`):

- `/health` - readiness (pode refletir dependências).
- `/alive` - liveness (não depende de DB/RabbitMQ).

Os serviços com `prometheus-net` (`UseHttpMetrics` + `MapMetrics`) expõem ainda:

- `/metrics` - métricas HTTP + as métricas custom `conexao_*`.

Cobertura atual de `/metrics`:

| Serviço | `/metrics` | Observação |
| --- | --- | --- |
| Identity API | sim | prometheus-net |
| Campaigns API | sim | prometheus-net |
| Donations Worker | sim | prometheus-net |
| Gateway (YARP) | sim | prometheus-net (RPS/latência de borda) |
| Web (Blazor) | não | apenas `/health` e `/alive`; scrape comentado no Prometheus |

## Métricas custom (`conexao_*`)

| Métrica | Tipo | Significado |
| --- | --- | --- |
| `conexao_donations_processed_total` | counter | doações processadas com sucesso pelo Worker |
| `conexao_donations_rejected_total` | counter | doações rejeitadas (campanha encerrada/cancelada) |
| `conexao_donation_publish_total` | counter | eventos de doação publicados no broker |
| `conexao_donation_publish_failures_total` | counter | falhas de publicação no broker |
| `conexao_outbox_pending_messages` | gauge | mensagens pendentes na outbox (backlog) |
| `conexao_donation_processing_duration_seconds` | histogram | tempo de processamento por doação |
| `conexao_dead_letter_messages` | gauge | mensagens na dead-letter queue |

Métricas HTTP (prometheus-net): `http_requests_received_total`,
`http_request_duration_seconds_{count,bucket,sum}`, `http_requests_in_progress`.

## Dashboards Grafana

Provisionados automaticamente a partir de `infra/grafana/dashboards/*.json`
(datasource e provisioning em `infra/grafana/provisioning/`). Pasta no Grafana:
**Conexão Solidária**.

### 1. Visão Executiva (Negócio) - `negocio.json`
Painéis: doações processadas (total), doações rejeitadas (total), outbox pendente,
dead-letter, taxa de processamento e acumulado ao longo do tempo.

```promql
sum(conexao_donations_processed_total)
sum(conexao_donations_rejected_total)
sum(rate(conexao_donations_processed_total[1m]))
sum(conexao_outbox_pending_messages)
sum(conexao_dead_letter_messages)
```

### 2. Aplicação (HTTP) - `aplicacao.json`
Painéis: RPS por serviço, latência p95 por serviço, taxa de erro 5xx (global e por
serviço), requisições em andamento.

```promql
sum(rate(http_requests_received_total[1m])) by (job)
histogram_quantile(0.95, sum(rate(http_request_duration_seconds_bucket[5m])) by (le, job))
sum(rate(http_request_duration_seconds_count{code=~"5.."}[5m])) / sum(rate(http_request_duration_seconds_count[5m]))
sum(http_requests_in_progress) by (job)
```

### 3. Mensageria - `mensageria.json`
Painéis: publish vs consume, tempo de processamento por doação (p50/p95), outbox
pendente, dead-letter, profundidade da fila RabbitMQ (requer plugin), backlog+DLQ ao
longo do tempo.

```promql
sum(rate(conexao_donation_publish_total[1m]))
sum(rate(conexao_donations_processed_total[1m]))
histogram_quantile(0.95, sum(rate(conexao_donation_processing_duration_seconds_bucket[5m])) by (le))
sum(conexao_dead_letter_messages)
sum(rabbitmq_queue_messages{queue="doacoes-recebidas"})   # requer plugin rabbitmq_prometheus
```

## Alertas (Grafana Unified Alerting)

Provisionados em `infra/grafana/provisioning/alerting/conexao-solidaria-rules.yaml`
(pasta **Conexão Solidária**). Pipeline padrão: query instant (A) → reduce last (B) →
threshold (C).

| Alerta | Severidade | Condição |
| --- | --- | --- |
| DLQ com mensagens | critical | `sum(conexao_dead_letter_messages) > 0` (`for: 0m`) |
| Outbox pendente alto | warning | `sum(conexao_outbox_pending_messages) > 20` por `2m` |
| Taxa de erro 5xx alta | warning | fração de 5xx `> 0.05` por `2m` |

Expressão da taxa de 5xx:

```promql
sum(rate(http_request_duration_seconds_count{code=~"5.."}[5m])) / sum(rate(http_request_duration_seconds_count[5m]))
```

## Prometheus

Config em `infra/prometheus/prometheus.yml` (`scrape_interval: 5s`). Jobs:
`identity-api`, `campaigns-api`, `donations-worker`, `gateway` (todos `:8080/metrics`)
e `rabbitmq` (`:15692`, **requer** o plugin `rabbitmq_prometheus`). O job `web` está
comentado (o Web não expõe `/metrics`).

Validar targets: `Status > Targets`, ou:

```promql
up
up{job="identity-api"}
```

> No Docker Compose o `gateway` não é um serviço; o target `gateway` aparecerá **DOWN**
> nesse ambiente (ele existe no Kubernetes/Aspire). O `rabbitmq` só fica UP com o plugin
> `rabbitmq_prometheus` habilitado.

## Zabbix (template real)

Template importável: `infra/zabbix/templates/conexao-solidaria-template.yaml` (export
nativo Zabbix 7.0, compatível 6.0+). Guia completo em `infra/zabbix/README.md`.

**O que monitora:**
- `/health` de Identity, Campaigns e Worker via **web monitoring** (rspcode + latência) -
 gera `web.test.fail[...]` e `web.test.time[...,resp]`.
- Vitrine pública `GET /api/campanhas/transparencia` (rspcode).
- **Profundidade da fila** `doacoes-recebidas` e da **DLQ** `doacoes.dead-letter` via
  **RabbitMQ Management API** (`/api/queues/%2f/<fila>`, campo `.messages`), itens
  `HTTP_AGENT` com auth básica.

**9 triggers:** indisponibilidade das 3 APIs/Worker por 2min (HIGH), latência `/health`
acima de `{$HTTP.LATENCY.MAX}` (WARNING), vitrine de transparência com erro (AVERAGE),
fila principal acima de `{$QUEUE.DEPTH.MAX}` por 2min (HIGH) e DLQ `> 0` (HIGH).

**Importar e associar:**
1. `Data collection > Templates > Import` → selecione o arquivo.
2. `Data collection > Hosts > Create host` → aba `Templates` → link "Conexão Solidária".
3. Aba `Macros` → ajuste principalmente `{$RABBITMQ.USER}` e `{$RABBITMQ.PASSWORD}`
   (as URLs default apontam para os nomes de serviço `http://identity-api:8080`,
   `http://rabbitmq:15672`, etc., alcançáveis pelo Zabbix server na rede interna).

## Tracing distribuído (OpenTelemetry → Tempo)

Todos os serviços instrumentam traces via `ServiceDefaults` (OpenTelemetry). O pipeline
fora do Aspire é: **serviços (.NET) → OTel Collector → Tempo → Grafana (Explore)**.

### Fluxo

```text
identity-api ─┐
campaigns-api ─┼─ OTLP ─► otel-collector ─► otlp/tempo (tempo:4317) ─► Tempo ─► Grafana (datasource Tempo)
donations-worker ─┘                       └─ prometheus (:8889) ─────► Prometheus (métricas OTLP)
```

O contexto de trace é propagado ponta a ponta: o Gateway injeta `X-Correlation-Id` e o
`traceparent` viaja pelo HTTP e pela mensageria (header `traceparent` no evento
`DoacaoRecebidaEvent`), então um span do `POST /api/doacoes` se conecta ao processamento
no Worker.

### OTel Collector

Config em `infra/otel/otel-collector-config.yaml` (imagem `otel/opentelemetry-collector-contrib`);
detalhes em `infra/otel/README.md`. Recebe OTLP (gRPC `:4317` / HTTP `:4318`), aplica
`memory_limiter`/`resource`/`batch` e exporta em **três pipelines**:

- **traces** → `otlp/tempo` (`tempo:4317`, `tls.insecure` no demo) + `debug` (stdout);
- **metrics** → `prometheus` (expostas em `:8889/metrics` para o Prometheus fazer scrape)
  + `debug`;
- **logs** → `debug`.

O processor `resource` marca todos os sinais com `deployment.environment=conexao-solidaria`.
Health do próprio collector em `:13133`.

### Tempo (backend de traces)

`infra/tempo/tempo.yaml` (imagem `grafana/tempo`). Recebe traces via OTLP do collector
(`:4317`) e serve a **API de consulta em `:3200`**, usada pelo datasource do Grafana.

### Datasource Grafana

`infra/grafana/provisioning/datasources/tempo.yml` provisiona o datasource **Tempo**
(uid `Tempo`, `url: http://tempo:3200`). Ele já vem com:

- `tracesToMetrics` → correlaciona um trace com as métricas do serviço (exemplars),
  reutilizando o datasource `Prometheus`;
- `serviceMap` → mapa de serviços (também sobre o Prometheus).

Abra **Grafana → Explore → datasource Tempo** e busque por trace ID, service ou nome de
span (ex.: filtrar por `service.name`).

### Ligando o exporter (envs)

O `AddOpenTelemetryExporters()` do `ServiceDefaults` só liga o exporter OTLP quando
`OTEL_EXPORTER_OTLP_ENDPOINT` está definido. Sem essa variável, a telemetria OTLP vai
apenas para o **Aspire Dashboard** local.

- **Docker Compose:** já configurado. `identity-api`, `campaigns-api` e `donations-worker`
  recebem `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` e
  `OTEL_EXPORTER_OTLP_PROTOCOL=grpc`; os serviços `otel-collector` e `tempo` sobem no mesmo
  `docker compose up`.
- **Manual / outro ambiente:**

  ```powershell
  $env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://otel-collector:4317"   # gRPC
  # ou HTTP: http://otel-collector:4318 + OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
  ```

- **Kubernetes:** os deployments **não** definem `OTEL_EXPORTER_OTLP_ENDPOINT` por padrão
  (o overlay foca em métricas Prometheus + Grafana). Para tracing no cluster, adicione
  `otel-collector` + `tempo` e a env acima apontando para o Service do collector.

## Como abrir cada ferramenta

### Dev local (Aspire)
`dotnet run` no `ConexaoSolidaria.AppHost` sobe o grafo e o **Aspire Dashboard** (traces,
métricas e logs OTLP), sem configuração extra.

### Docker Compose
```powershell
docker compose up --build          # (-d para segundo plano)
docker compose ps
docker compose down                # (-v para apagar volumes)
```

| Ferramenta | URL | Credenciais |
| --- | --- | --- |
| Prometheus | http://localhost:9090 | não requer |
| Grafana | http://localhost:3000 | `.env`: `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` |
| Zabbix Web | http://localhost:8085 | `Admin` / `.env`: `ZABBIX_PASSWORD` |
| Identity API | http://localhost:5001 (`/metrics`, `/health`, `/swagger`) | - |
| Campaigns API | http://localhost:5002 | - |
| Donations Worker | http://localhost:5003 | - |
| Elasticsearch | http://localhost:9200 | - |
| RabbitMQ Management | http://localhost:15672 | `.env`: `RABBITMQ_USER` / `RABBITMQ_PASSWORD` |
| Tempo (API de consulta) | http://localhost:3200 | traces via **Grafana → Explore → Tempo** |
| OTel Collector (health) | http://localhost:13133 | métricas em `:8889/metrics` |

Credenciais reais vêm do `.env` (veja `.env.example` e `SECURITY.md`). O primeiro startup
do Zabbix pode demorar (inicializa as tabelas no Postgres). Os **traces** não se navegam
direto no Tempo: abra o **Grafana → Explore → datasource Tempo**.

### Kubernetes (Kustomize)
No Kubernetes a stack de observabilidade fica **ClusterIP**; o acesso é via `port-forward`
(deploy completo em `ReadmeKubernetes.md`).

> **O `pwsh infra/k8s/up.ps1` já deixa Grafana (`3000`), Prometheus (`9090`) e RabbitMQ
> (`15672`) liberados automaticamente** em segundo plano — as URLs acima funcionam sem nenhum
> comando extra. **O Zabbix não entra na lista automática** e precisa do forward manual.

```powershell
# Zabbix: necessário (não é automático)
kubectl port-forward -n conexao-solidaria svc/zabbix-web  8085:8080

# Equivalentes manuais dos que o up.ps1 já sobe (use com -NoForward, por exemplo)
kubectl port-forward -n conexao-solidaria svc/grafana     3000:3000
kubectl port-forward -n conexao-solidaria svc/prometheus  9090:9090
kubectl port-forward -n conexao-solidaria svc/rabbitmq   15672:15672
```

Credenciais vêm do Secret `conexao-solidaria-secret` (`grafana-admin-*`, `rabbitmq-*`,
`zabbix-*`).

## Gerando métricas para demonstração

1. Login do gestor na Identity (`gestor@conexaosolidaria.local`, senha do Secret/`.env`).
2. Autorize na Campaigns API com o token e crie uma campanha ativa.
3. Cadastre um doador na Identity e faça `POST /api/doacoes` com o token do doador.
4. Observe: **Grafana** (os 3 dashboards), **Prometheus** (`up`, `conexao_*`), **RabbitMQ**
   (fila `doacoes-recebidas`) e **Zabbix** (disponibilidade + profundidade de fila).

Para demonstrar falha/recuperação (parar o Worker, ver a fila crescer, o alerta de DLQ e a
retomada), siga `docs/cenario-falha-recuperacao.md`. Para responder a incidentes (DLQ,
outbox travada, 5xx), siga `docs/runbook.md`.

## Troubleshooting

- **Target DOWN no Prometheus:** confira `docker compose ps` e teste
  `curl http://localhost:5001/metrics` (5002/5003). Lembre: `gateway` fica DOWN no Compose
  e `rabbitmq` exige o plugin `rabbitmq_prometheus`.
- **Grafana sem dados:** valide o datasource `Prometheus`, rode `up` no Prometheus e gere
  tráfego. Após editar provisioning: `docker compose restart grafana`.
- **Painel de fila RabbitMQ vazio:** o plugin `rabbitmq_prometheus` não está habilitado -
  os painéis `conexao_*` (outbox/DLQ) funcionam mesmo sem ele.
- **Zabbix não abre:** primeira inicialização é lenta; veja
  `docker compose logs -f zabbix-server zabbix-web postgres`. Se o banco corromper em testes
  locais: `docker compose down -v && docker compose up --build`.
- **Zabbix sem coletar filas:** preencha `{$RABBITMQ.USER}`/`{$RABBITMQ.PASSWORD}` no host e
  confirme que o Zabbix server alcança `http://rabbitmq:15672`.
