# Observabilidade

Este documento descreve a stack de observabilidade do Conexao Solidaria, o
catalogo completo de metricas expostas, os dashboards do Grafana, os alertas e
como tudo e conectado em Docker Compose e em Kubernetes.

Documentos relacionados: [arquitetura.md](arquitetura.md),
[runbook.md](runbook.md), [cenario-falha-recuperacao.md](cenario-falha-recuperacao.md).

---

## 1. Visao geral da stack

| Pilar | Ferramenta | Onde |
|---|---|---|
| Metricas | **Prometheus** (scrape de `/metrics`) | `infra/prometheus/` (compose) e ConfigMap `prometheus-config` (k8s) |
| Dashboards / alertas | **Grafana** 11.4 | `infra/grafana/` (compose) e ConfigMaps `grafana-*` (k8s) |
| Traces | **OpenTelemetry -> OTel Collector -> Tempo** | `infra/otel/`, `infra/tempo/` |
| Broker | **RabbitMQ** + plugin `rabbitmq_prometheus` | `infra/rabbitmq/` (compose) e `rabbitmq.yaml` (k8s) |
| Monitoramento complementar | **Zabbix** (template HTTP + triggers) | `infra/zabbix/` |
| Correlacao | `X-Correlation-Id` (Gateway) + `traceparent` (ate o Worker) | ServiceDefaults |

Fluxo de metricas: cada servico .NET expoe `/metrics` (biblioteca
**prometheus-net**, via `app.UseHttpMetrics()` + `app.MapMetrics()`); o Prometheus
faz scrape a cada **5s**; o Grafana consulta o Prometheus (datasource `Prometheus`)
e o Tempo (datasource `Tempo`).

Servicos que expoem `/metrics`: `identity-api`, `campaigns-api`,
`donations-worker`, `gateway`. O RabbitMQ expoe metricas nativas do broker em
`:15692` (plugin `rabbitmq_prometheus`).

---

## 2. Catalogo de metricas

### 2.1. Metricas de negocio / mensageria (custom `conexao_*`)

Definidas no codigo com prometheus-net. **Registro tardio (importante):** esses
contadores/histograma so aparecem no `/metrics` **apos o primeiro uso** (a primeira
doacao processada); nao sao publicados em zero no boot. E o comportamento padrao do
prometheus-net para campos `static readonly` com `beforefieldinit`.

| Metrica | Tipo | Labels | Servico | Significado |
|---|---|---|---|---|
| `conexao_donations_processed_total` | Counter | - | Worker | Doacoes processadas com sucesso |
| `conexao_donations_rejected_total` | Counter | - | Worker / Campaigns | Doacoes rejeitadas (campanha encerrada/cancelada) |
| `conexao_dead_letter_messages` | Counter | - | Worker | Mensagens enviadas para a DLQ |
| `conexao_donation_processing_duration_seconds` | Histogram | - | Worker | Duracao do processamento por doacao (buckets -> p50/p95/p99) |
| `conexao_outbox_pending_messages` | Gauge | - | Campaigns | Eventos na outbox aguardando publicacao |
| `conexao_donation_publish_total` | Counter | - | Campaigns | Eventos publicados no broker com sucesso |
| `conexao_donation_publish_failures_total` | Counter | - | Campaigns | Falhas ao publicar no broker |
| `conexao_donation_amount_brl_total` | Counter | - | Worker | **Valor total arrecadado (R$)** em doacoes processadas |
| `conexao_donations_by_campaign_total` | Counter | `campanha` | Worker | **Doacoes processadas por campanha** |
| `conexao_donation_amount_by_campaign_brl_total` | Counter | `campanha` | Worker | **Valor arrecadado por campanha (R$)** |

As tres ultimas sao as metricas de valor/campanha adicionadas para a visao
executiva. Sao incrementadas **apos o commit** da transacao (fora do bloco
reexecutavel da execution strategy), para nao contar em dobro em retry
transitorio. O label `campanha` usa o **titulo** da campanha.

> **Cardinalidade:** o label `campanha` cresce com o numero de campanhas ativas.
> Adequado para a POC. Em producao com muitas campanhas, considerar limitar
> (top-N) ou usar o id no lugar do titulo.

### 2.2. Metricas HTTP (prometheus-net `UseHttpMetrics`)

Emitidas por todos os servicos com pipeline HTTP (APIs + Gateway). O label `job`
e adicionado pelo Prometheus (nome do scrape).

| Metrica | Tipo | Labels | Uso |
|---|---|---|---|
| `http_requests_received_total` | Counter | `code`, `method`, `controller`, `action` | RPS, top rotas |
| `http_request_duration_seconds` (`_bucket`/`_count`/`_sum`) | Histogram | `code`, `method`, `controller`, `action` | Latencia p50/p90/p99, Apdex, heatmap, taxa de erro |
| `http_requests_in_progress` | Gauge | `code`, `method`, `controller`, `action` | Concorrencia / saturacao |

### 2.3. Metricas de runtime (.NET / processo)

Expostas por padrao pelo prometheus-net (nao ha `SuppressDefaultMetrics`). Usadas
no dashboard de Saude para CPU, memoria, threads, GC e uptime.

| Metrica | Tipo | Uso |
|---|---|---|
| `process_cpu_seconds_total` | Counter | CPU em cores (`rate(...)`) |
| `process_working_set_bytes` | Gauge | Memoria residente |
| `process_num_threads` | Gauge | Threads |
| `process_start_time_seconds` | Gauge | Uptime (`time() - ...`) e reinicios (`changes(...)`) |
| `dotnet_total_memory_bytes` | Gauge | Heap gerenciado |
| `dotnet_collection_count_total` | Counter (`generation`) | Pressao de GC |

### 2.4. Metricas do Prometheus (meta)

| Metrica | Uso |
|---|---|
| `up{job}` | **Saude rodando/parado** de cada alvo (1 = up, 0 = down) |
| `scrape_duration_seconds{job}` | Latencia/saude da coleta por alvo |

### 2.5. Metricas do RabbitMQ (plugin `rabbitmq_prometheus`)

Expostas em `rabbitmq:15692`. Requerem o plugin habilitado (ver secao 5).

| Metrica | Uso |
|---|---|
| `rabbitmq_queue_messages{queue}` | Total de mensagens na fila |
| `rabbitmq_queue_messages_ready{queue}` | **Esperando na fila** (prontas, nao entregues) |
| `rabbitmq_queue_messages_unacked{queue}` | **Em processamento** (entregues, sem ack) |
| `rabbitmq_queue_consumers{queue}` | Consumidores ativos |

Fila principal de doacoes: `doacoes-recebidas`.

---

## 3. Dashboards do Grafana

Pasta **Conexao Solidaria**. Fonte unica: `infra/grafana/dashboards/*.json`
(refresh 5s, janela padrao 30 min).

### 3.1. Aplicacao (HTTP) - `conexao-solidaria-aplicacao`
Visao de borda e das APIs.
- **KPIs:** RPS total, latencia p95, gauge de erro 5xx, gauge **Apdex** (T=0.25s),
  requests em andamento, erro 4xx.
- **Trafego:** RPS por servico; bargauge de **top rotas** por RPS.
- **Latencia:** percentis **p50/p90/p99**; **heatmap** de distribuicao.
- **Erros:** codigos de status por classe (2xx/3xx/4xx/5xx empilhados); taxa de
  5xx por servico; requisicoes em andamento por servico.

### 3.2. Visao Executiva (Negocio) - `conexao-solidaria-negocio`
Indicadores de doacoes e arrecadacao.
- **KPIs:** doacoes processadas, rejeitadas, gauge de **taxa de aceitacao**, vazao
  atual, DLQ.
- **Composicao:** taxa de processamento (processadas x rejeitadas), donut
  aceitas x rejeitadas, taxa de rejeicao.
- **Acumulados:** doacoes acumuladas, p50/p95 de processamento.
- **Arrecadacao (R$):** total arrecadado, **ticket medio**, arrecadacao acumulada,
  **ranking de campanhas** por valor e por numero de doacoes.

### 3.3. Mensageria - `conexao-solidaria-mensageria`
Pipeline Outbox + RabbitMQ.
- Fluxo publish x consume; tempo de processamento (p50/p95); outbox pendente; DLQ;
  profundidade da fila; backlog ao longo do tempo.
- Fila **ready** (esperando), **unacked** (em processamento), **consumidores**,
  **taxa de sucesso na publicacao** e composicao da fila.

### 3.4. Saude & Infraestrutura - `conexao-solidaria-saude`
Saude operacional dos servicos.
- **Status:** servicos no ar, saude **RODANDO/PARADO** por servico, **state-timeline**
  de disponibilidade (mostra exatamente quando cada servico caiu).
- **Uptime e reinicios** por servico.
- **Fila/backlog:** aguardando publicacao (outbox), esperando na fila (ready), em
  processamento (unacked), consumidores ativos, backlog ao longo do tempo.
- **Recursos:** CPU, memoria (working set), threads, GC .NET, heap gerenciado.
- **Coleta:** duracao do scrape por alvo.

---

## 4. Alertas

Provisionados em `infra/grafana/provisioning/alerting/conexao-solidaria-rules.yaml`
(Grafana Unified Alerting, avaliacao a cada 30s):

| Alerta | Condicao | Severidade |
|---|---|---|
| DLQ com mensagens | `sum(conexao_dead_letter_messages) > 0` | critical |
| Outbox pendente alto | `sum(conexao_outbox_pending_messages) > 20` por 2 min | warning |
| Erro HTTP 5xx alto | taxa de 5xx `> 5%` por 2 min | warning |

---

## 5. Conexao da stack por ambiente

### 5.1. Docker Compose
- **Prometheus:** `infra/prometheus/prometheus.yml` (scrape de identity/campaigns/
  worker/gateway/rabbitmq/otel-collector).
- **Grafana:** dashboards e provisioning montados de `infra/grafana/` via volume.
- **RabbitMQ:** plugin habilitado por `infra/rabbitmq/enabled_plugins`
  (`[rabbitmq_management,rabbitmq_prometheus].`), porta `15692` publicada.

### 5.2. Kubernetes (`infra/k8s/base/`)
A observabilidade e reprovisionada via **ConfigMap** (armazenamento efemero):
- **`observability.yaml`:** ConfigMap `prometheus-config` (scrape de identity/
  campaigns/worker/**gateway**/**rabbitmq**); Deployment do Prometheus; ConfigMap
  `grafana-provisioning` (datasource + provider de dashboards); Deployment do
  Grafana montando o diretorio de dashboards.
- **`grafana-dashboards.yaml`:** ConfigMap `grafana-dashboards`, **gerado** a partir
  de `infra/grafana/dashboards/*.json`, montado em `/var/lib/grafana/dashboards`.
- **`rabbitmq.yaml`:** ConfigMap `rabbitmq-enabled-plugins` montado em
  `/etc/rabbitmq/enabled_plugins`; porta `15692` no container e no Service.
- **`network-policies.yaml`:** libera o Prometheus para scrapear
  `rabbitmq:15692` e `gateway:8080` (default-deny-ingress + allow-list).

> **Prometheus e subPath:** o `prometheus.yml` e montado via `subPath`, que **nao**
> propaga alteracoes do ConfigMap para o pod em execucao. Apos mudar o scrape
> config, reinicie o Prometheus:
> `kubectl rollout restart deployment/prometheus -n conexao-solidaria`.

#### Regerar o ConfigMap de dashboards (k8s)
`grafana-dashboards.yaml` e derivado dos JSONs. Ao editar um dashboard, regenere e
reaplique:

```bash
# na raiz do repo
node -e '
const fs=require("fs");
const dir="infra/grafana/dashboards";
const files=["aplicacao.json","negocio.json","mensageria.json","saude.json"];
let out="apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: grafana-dashboards\ndata:\n";
for(const f of files){
  const c=fs.readFileSync(dir+"/"+f,"utf8").replace(/\r\n/g,"\n").replace(/\s+$/,"");
  out+="  "+f+": |\n"+c.split("\n").map(l=>"    "+l).join("\n")+"\n";
}
fs.writeFileSync("infra/k8s/base/grafana-dashboards.yaml",out);
'
kubectl apply -k infra/k8s/overlays/local
kubectl rollout restart deployment/grafana -n conexao-solidaria
```

---

## 6. Acesso (demo)

Compose: Grafana em `http://localhost:3000`, Prometheus em `http://localhost:9090`.

Kubernetes (via `kubectl port-forward`, que nao passa pela NetworkPolicy):

```bash
kubectl port-forward -n conexao-solidaria svc/grafana    3000:3000
kubectl port-forward -n conexao-solidaria svc/prometheus 9090:9090
kubectl port-forward -n conexao-solidaria svc/rabbitmq  15672:15672   # UI management
```

Credenciais do Grafana: `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` do `.env`
(no k8s, do Secret `conexao-solidaria-secret`).

---

## 7. Verificacao rapida

```bash
# Alvos do Prometheus (todos devem estar "up")
curl -s 'http://localhost:9090/api/v1/targets?state=active' \
  | grep -o '"job":"[^"]*","[^}]*"health":"[^"]*"'

# Metricas de negocio (aparecem apos a 1a doacao processada)
curl -s 'http://localhost:9090/api/v1/query?query=conexao_donation_amount_brl_total'
curl -s 'http://localhost:9090/api/v1/query?query=sum(conexao_donations_by_campaign_total)by(campanha)'

# Fila (requer plugin rabbitmq_prometheus)
curl -s 'http://localhost:9090/api/v1/query?query=rabbitmq_queue_messages_ready'
```
