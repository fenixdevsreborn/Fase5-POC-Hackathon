# Zabbix - Monitoracao real da Conexao Solidaria

O `docker-compose.yml` e o manifesto k8s (`infra/k8s/conexao-solidaria.yaml`) ja
sobem o `zabbix-server` (PostgreSQL backend, banco `zabbixdb`) e o `zabbix-web`.
Este diretorio adiciona o **template de monitoracao** que transforma o Zabbix de
"instalado" em "monitorando de fato".

## Acesso a interface

| Ambiente        | URL                          | Login padrao |
|-----------------|------------------------------|--------------|
| docker-compose  | http://localhost:8085        | `Admin` / `zabbix` |
| kubernetes      | http://localhost:8085        | `Admin` / `zabbix` |

No Kubernetes o `zabbix-web` e ClusterIP e **nao** entra nos port-forwards automaticos do
`up.ps1` — abra o dele manualmente:

```powershell
kubectl port-forward -n conexao-solidaria svc/zabbix-web 8085:8080
```

## O que o template monitora

`templates/conexao-solidaria-template.yaml` (export nativo Zabbix 7.0):

| Item / Web scenario                              | Fonte                                                        |
|--------------------------------------------------|-------------------------------------------------------------|
| Identity API health (rspcode + tempo)            | `GET {$IDENTITY.URL}/health`                                 |
| Campaigns API health (rspcode + tempo)           | `GET {$CAMPAIGNS.URL}/health`                                |
| Donations Worker health (rspcode + tempo)        | `GET {$WORKER.URL}/health`                                   |
| Vitrine transparencia (rspcode)                  | `GET {$CAMPAIGNS.URL}/api/campanhas/transparencia`          |
| Fila `doacoes-recebidas` (profundidade)          | RabbitMQ API `/api/queues/%2f/doacoes-recebidas` -> `.messages` |
| DLQ `doacoes.dead-letter` (profundidade)         | RabbitMQ API `/api/queues/%2f/doacoes.dead-letter` -> `.messages` |

Os health checks usam **Web monitoring** (httptest), que gera automaticamente os
itens `web.test.rspcode[...]`, `web.test.time[...,resp]` e `web.test.fail[...]`.
As profundidades de fila usam **HTTP agent** com autenticacao Basic.

### Triggers

| Trigger                                             | Severidade | Condicao |
|-----------------------------------------------------|------------|----------|
| Identity API indisponivel por 2min                  | HIGH       | health falhando por 2m |
| Campaigns API indisponivel por 2min                 | HIGH       | health falhando por 2m |
| Donations Worker indisponivel por 2min              | HIGH       | health falhando por 2m |
| Identity/Campaigns/Worker lenta: /health > 1s       | WARNING    | `web.test.time > {$HTTP.LATENCY.MAX}` por 2m |
| Fila doacoes-recebidas acima de 20 por 2min         | HIGH       | `messages > {$QUEUE.DEPTH.MAX}` por 2m |
| DLQ doacoes.dead-letter com mensagens (>0)          | HIGH       | `messages > 0` |
| Vitrine de transparencia retornando erro            | AVERAGE    | rspcode != 200 por 2m |

## Passo a passo

### 1. Importar o template

Data collection -> Templates -> **Import** -> selecione
`infra/zabbix/templates/conexao-solidaria-template.yaml` -> Import.
Cria o template `Conexao Solidaria` e o grupo `Templates/Applications`.

### 2. Criar o host e linkar

1. Data collection -> Hosts -> **Create host**
   - Host name: `conexao-solidaria`
   - Host groups: crie/escolha `Conexao Solidaria/App`
   - Interface: como todos os itens sao HTTP agent / web (executados pelo
     Zabbix server), a interface de agente e apenas formal. Use `127.0.0.1:10050`.
2. Aba **Templates** -> Link new templates -> `Conexao Solidaria`.
3. Aba **Macros** -> Add / Inherited and host macros.

### 3. Configurar macros (credenciais do RabbitMQ)

As unicas macros obrigatorias por ambiente sao as credenciais do RabbitMQ
Management. Use os mesmos valores de `RABBITMQ_USER` / `RABBITMQ_PASSWORD` do
`.env` (ou do Secret `conexao-solidaria-secret` no k8s):

| Macro                 | Valor tipico (compose)          | Observacao |
|-----------------------|---------------------------------|------------|
| `{$RABBITMQ.USER}`     | `RABBITMQ_USER` do `.env`        | usuario da Management API |
| `{$RABBITMQ.PASSWORD}` | `RABBITMQ_PASSWORD` do `.env`    | tipo SECRET (mascarado na UI) |
| `{$IDENTITY.URL}`      | `http://identity-api:8080`       | so ajuste se a rede mudar |
| `{$CAMPAIGNS.URL}`     | `http://campaigns-api:8080`      | so ajuste se a rede mudar |
| `{$WORKER.URL}`        | `http://donations-worker:8080`   | so ajuste se a rede mudar |
| `{$RABBITMQ.API.URL}`  | `http://rabbitmq:15672`          | Management HTTP API |
| `{$QUEUE.DEPTH.MAX}`   | `20`                             | limite de backlog |
| `{$HTTP.LATENCY.MAX}`  | `1`                              | limite de latencia (s) |

> **Rede:** o `zabbix-server` resolve os nomes de servico (`identity-api`,
> `campaigns-api`, `rabbitmq`, ...) por estar na mesma rede do compose/k8s.
> Se rodar o Zabbix fora dessa rede, aponte as macros de URL para as portas
> publicadas no host (ex.: `http://host.docker.internal:5001` para a Identity,
> `:5002` Campaigns, `:5003` Worker, `:15672` RabbitMQ).

### 4. Validar

- Monitoring -> Latest data -> filtre pelo host `conexao-solidaria`. Os itens de
  fila e os web scenarios devem coletar em ate ~1 minuto.
- Monitoring -> Problems: sem alertas com tudo saudavel. Para testar, escale o
  Worker para 0 replicas (ver `docs/cenario-falha-recuperacao.md`) e observe a
  trigger "Fila doacoes-recebidas acima de 20 por 2min" disparar.

## Nomes reais usados (referencia)

- Filas RabbitMQ: `doacoes-recebidas` (principal), `doacoes.dead-letter` (DLQ),
  `doacoes.retry.10s` / `doacoes.retry.60s` (retry). Vhost `/` = `%2f` na API.
- Health endpoint das APIs/worker: `/health` (porta interna `8080`).
- Endpoint publico: `GET /api/campanhas/transparencia`.
