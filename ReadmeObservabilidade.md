# Observabilidade

Este documento explica como rodar, acessar e configurar a observabilidade do projeto Conexao Solidaria usando Docker Desktop. A stack inclui:

- Prometheus para coleta de metricas.
- Grafana para dashboards.
- Zabbix para monitoramento complementar de disponibilidade.

As aplicacoes `.NET 10` expoem:

- `/health`: saude do servico.
- `/metrics`: metricas no formato Prometheus.

## Servicos monitorados

| Servico | Compose | Porta local | Endpoint de saude | Endpoint de metricas |
| --- | --- | --- | --- | --- |
| Identity API | `identity-api` | `5001` | http://localhost:5001/health | http://localhost:5001/metrics |
| Campaigns API | `campaigns-api` | `5002` | http://localhost:5002/health | http://localhost:5002/metrics |
| Donations Worker | `donations-worker` | `5003` | http://localhost:5003/health | http://localhost:5003/metrics |

Dentro da rede Docker Compose, os mesmos servicos sao acessados por:

- `http://identity-api:8080`
- `http://campaigns-api:8080`
- `http://donations-worker:8080`

## Rodando com Docker Compose

Suba toda a stack:

```powershell
docker compose up --build
```

Para rodar em segundo plano:

```powershell
docker compose up --build -d
```

Confira se os containers subiram:

```powershell
docker compose ps
```

Veja logs da stack de observabilidade:

```powershell
docker compose logs -f prometheus grafana zabbix-server zabbix-web
```

Para parar tudo:

```powershell
docker compose down
```

Para limpar tambem os volumes locais, incluindo dados de Grafana, Zabbix e Postgres:

```powershell
docker compose down -v
```

## Acessos locais

| Ferramenta | URL | Usuario | Senha |
| --- | --- | --- | --- |
| Prometheus | http://localhost:9090 | Nao requer | Nao requer |
| Grafana | http://localhost:3000 | `admin` | `admin` |
| Zabbix | http://localhost:8085 | `Admin` | `zabbix` |

O primeiro startup do Zabbix pode demorar alguns minutos porque ele inicializa as tabelas no PostgreSQL.

## Prometheus

### Como esta configurado

O Prometheus usa o arquivo:

```text
infra/prometheus/prometheus.yml
```

Configuracao atual:

```yaml
global:
  scrape_interval: 5s

scrape_configs:
  - job_name: identity-api
    metrics_path: /metrics
    static_configs:
      - targets: ["identity-api:8080"]

  - job_name: campaigns-api
    metrics_path: /metrics
    static_configs:
      - targets: ["campaigns-api:8080"]

  - job_name: donations-worker
    metrics_path: /metrics
    static_configs:
      - targets: ["donations-worker:8080"]
```

O Prometheus roda no Compose com:

```yaml
prometheus:
  image: prom/prometheus:v2.55.1
  ports:
    - "9090:9090"
  volumes:
    - ./infra/prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
```

### Validando os targets

1. Acesse http://localhost:9090.
2. Abra `Status` > `Targets`.
3. Verifique se os jobs aparecem como `UP`:
   - `identity-api`
   - `campaigns-api`
   - `donations-worker`

Tambem e possivel consultar na aba `Graph`:

```promql
up
```

Ou por servico:

```promql
up{job="identity-api"}
up{job="campaigns-api"}
up{job="donations-worker"}
```

### Consultas uteis

Requisicoes HTTP por segundo:

```promql
sum(rate(http_requests_received_total[1m])) by (job)
```

Total de requisicoes HTTP recebidas:

```promql
sum(http_requests_received_total) by (job)
```

Doacoes processadas pelo worker:

```promql
conexao_donations_processed_total
```

Doacoes rejeitadas pelo worker:

```promql
conexao_donations_rejected_total
```

### Alterando a configuracao

Para adicionar outro servico ao Prometheus:

1. Edite `infra/prometheus/prometheus.yml`.
2. Adicione um novo item em `scrape_configs`.
3. Reinicie o Prometheus:

```powershell
docker compose restart prometheus
```

Exemplo:

```yaml
- job_name: novo-servico
  metrics_path: /metrics
  static_configs:
    - targets: ["novo-servico:8080"]
```

## Grafana

### Como esta configurado

O Grafana e provisionado automaticamente por estes arquivos:

```text
infra/grafana/provisioning/datasources/prometheus.yml
infra/grafana/provisioning/dashboards/dashboards.yml
infra/grafana/dashboards/conexao-solidaria.json
```

O datasource `Prometheus` aponta para:

```text
http://prometheus:9090
```

Esse endereco funciona dentro da rede Docker Compose. Pelo navegador da maquina, o Prometheus fica em `http://localhost:9090`.

### Acessando

1. Acesse http://localhost:3000.
2. Login:
   - Usuario: `admin`
   - Senha: `admin`
3. Se o Grafana pedir troca de senha, escolha uma nova senha ou pule a troca para ambiente local.
4. Abra `Dashboards`.
5. Entre na pasta `Conexao Solidaria`.
6. Abra o dashboard `Conexao Solidaria - Aplicacao`.

### Validando o datasource

1. Acesse `Connections` ou `Data sources`.
2. Abra o datasource `Prometheus`.
3. Clique em `Save & test`.
4. O retorno esperado e uma mensagem de sucesso na conexao.

### Paineis existentes

O dashboard provisionado mostra:

- Requisicoes HTTP por segundo por servico.
- Total de doacoes processadas pelo worker.
- Total de doacoes rejeitadas pelo worker.

Para gerar dados no dashboard, use as APIs via Swagger:

- Identity Swagger: http://localhost:5001/swagger
- Campaigns Swagger: http://localhost:5002/swagger

### Criando um painel novo

1. Acesse http://localhost:3000.
2. Abra `Dashboards`.
3. Clique em `New` > `New dashboard`.
4. Clique em `Add visualization`.
5. Selecione o datasource `Prometheus`.
6. Use uma query PromQL.

Exemplo para disponibilidade dos servicos:

```promql
up
```

Exemplo para requisicoes HTTP:

```promql
sum(rate(http_requests_received_total[1m])) by (job)
```

Exemplo para doacoes processadas:

```promql
conexao_donations_processed_total
```

### Persistencia

O Grafana usa o volume Docker:

```yaml
grafana-data:
```

Dashboards criados pela interface ficam salvos nesse volume. Dashboards versionados no repositorio devem ficar em:

```text
infra/grafana/dashboards
```

Depois de editar arquivos de provisionamento, reinicie:

```powershell
docker compose restart grafana
```

## Zabbix

### Como esta configurado

O Compose sobe dois servicos Zabbix:

```yaml
zabbix-server:
  image: zabbix/zabbix-server-pgsql:alpine-latest

zabbix-web:
  image: zabbix/zabbix-web-nginx-pgsql:alpine-latest
```

O Zabbix usa o banco `zabbixdb`, criado pelo script:

```text
infra/postgres/init/01-create-databases.sql
```

Credenciais do banco:

- Database: `zabbixdb`
- Usuario: `zabbix`
- Senha: `zabbix`

### Acessando

1. Suba a stack com `docker compose up --build`.
2. Aguarde o Zabbix inicializar.
3. Acesse http://localhost:8085.
4. Login:
   - Usuario: `Admin`
   - Senha: `zabbix`

Se a tela inicial demorar ou retornar erro de banco, aguarde mais alguns minutos e confira:

```powershell
docker compose logs -f zabbix-server zabbix-web postgres
```

### Configurando monitoramento HTTP das APIs

Uma forma simples de demonstrar Zabbix neste projeto e monitorar os endpoints `/health` das APIs por HTTP.

Crie um host para a aplicacao:

1. Acesse o Zabbix.
2. Abra `Data collection` > `Hosts`.  
3. Clique em `Create host`.
4. Configure:
   - Host name: `Conexao Solidaria`
   - Groups: crie ou selecione `Applications`
   - Interfaces: pode manter sem agente se usar apenas itens HTTP.
5. Salve.

Crie um item para a Identity API:

1. Abra o host `Conexao Solidaria`.
2. Va em `Items`.
3. Clique em `Create item`.
4. Configure:
   - Name: `Identity API health`
   - Type: `HTTP agent`
   - URL: `http://identity-api:8080/health`
   - Request type: `GET`
   - Type of information: `Text`
   - Update interval: `30s`
5. Salve.

Crie itens equivalentes para:

```text
http://campaigns-api:8080/health
http://donations-worker:8080/health
```

Essas URLs funcionam porque o Zabbix esta dentro da mesma rede Docker Compose das APIs.

### Configurando triggers de indisponibilidade

Para cada item HTTP, crie uma trigger:

1. Abra o host `Conexao Solidaria`.
2. Va em `Triggers`.
3. Clique em `Create trigger`.
4. Exemplo para Identity:
   - Name: `Identity API indisponivel`
   - Severity: `High`
   - Expression: use o construtor de expressoes e selecione o item `Identity API health`.

Uma regra simples e disparar quando o endpoint nao retorna dado recente. Exemplo conceitual:

```text
nodata(/Conexao Solidaria/Identity API health,2m)=1
```

Repita para Campaigns API e Donations Worker.

### Configurando web scenario no Zabbix

Outra opcao e usar `Web scenarios`:

1. Abra o host `Conexao Solidaria`.
2. Va em `Web scenarios`.
3. Clique em `Create web scenario`.
4. Name: `Conexao Solidaria health checks`.
5. Adicione steps:
   - `Identity health`: `http://identity-api:8080/health`
   - `Campaigns health`: `http://campaigns-api:8080/health`
   - `Worker health`: `http://donations-worker:8080/health`
6. Expected status codes: `200`.
7. Update interval: `30s`.
8. Salve.

Com isso, o Zabbix passa a registrar disponibilidade e tempo de resposta.

## Kubernetes no Docker Desktop

Ative Kubernetes no Docker Desktop e selecione o contexto local:

```powershell
kubectl config use-context docker-desktop
```

Construa as imagens locais:

```powershell
docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile -t conexao-solidaria/identity-api:local .
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile -t conexao-solidaria/campaigns-api:local .
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile -t conexao-solidaria/donations-worker:local .
```

Suba os recursos:

```powershell
kubectl apply -f infra/k8s/conexao-solidaria.yaml
kubectl get pods -n conexao-solidaria
kubectl get svc -n conexao-solidaria
```

Acessos via NodePort:

| Ferramenta | URL |
| --- | --- |
| Prometheus | http://localhost:30090 |
| Grafana | http://localhost:30300 |
| Zabbix | http://localhost:30085 |
| Identity Swagger | http://localhost:30081/swagger |
| Campaigns Swagger | http://localhost:30082/swagger |
| RabbitMQ | http://localhost:31672 |

No Kubernetes, o Prometheus usa os services internos:

```text
identity-api:8080
campaigns-api:8080
donations-worker:8080
```

Para remover a stack do Kubernetes:

```powershell
kubectl delete -f infra/k8s/conexao-solidaria.yaml
```

## Gerando metricas para demonstracao

1. Acesse http://localhost:5001/swagger.
2. Faca login com o gestor:
   - Email: `gestor@conexaosolidaria.local`
   - Senha: `Gestor@123456`
3. Acesse http://localhost:5002/swagger.
4. Autorize com o token do gestor.
5. Crie uma campanha ativa.
6. Cadastre um doador na Identity API.
7. Autorize na Campaigns API com o token do doador.
8. Envie uma doacao em `POST /api/doacoes`.
9. Abra:
   - RabbitMQ para ver a fila.
   - Prometheus para consultar metricas.
   - Grafana para ver o dashboard.
   - Zabbix para ver disponibilidade dos endpoints.

## Troubleshooting

### Prometheus target DOWN

Confira se os containers estao rodando:

```powershell
docker compose ps
```

Teste o endpoint local:

```powershell
curl http://localhost:5001/metrics
curl http://localhost:5002/metrics
curl http://localhost:5003/metrics
```

Confira os logs:

```powershell
docker compose logs -f prometheus identity-api campaigns-api donations-worker
```

### Grafana sem dados

1. Verifique se Prometheus esta acessivel em http://localhost:9090.
2. No Prometheus, rode:

```promql
up
```

3. No Grafana, valide o datasource `Prometheus`.
4. Gere trafego nas APIs acessando os Swagger.
5. Reinicie o Grafana se alterou provisionamento:

```powershell
docker compose restart grafana
```

### Zabbix nao abre

O Zabbix pode demorar na primeira inicializacao. Confira logs:

```powershell
docker compose logs -f zabbix-server zabbix-web postgres
```

Se o banco ficou inconsistente durante testes locais, recrie os volumes:

```powershell
docker compose down -v
docker compose up --build
```

### Porta em uso

Portas usadas pela observabilidade:

- Prometheus: `9090`
- Grafana: `3000`
- Zabbix Web: `8085`
- Zabbix Server: `10051`

Se alguma estiver ocupada, altere o mapeamento em `docker-compose.yml`.

### Docker Desktop desligado

Se aparecer erro parecido com `dockerDesktopLinuxEngine`, abra o Docker Desktop e aguarde o engine ficar pronto. Depois rode:

```powershell
docker compose ps
```

### Contexto Kubernetes errado

Se `kubectl` tentar acessar outro cluster, selecione o Docker Desktop:

```powershell
kubectl config get-contexts
kubectl config use-context docker-desktop
```
