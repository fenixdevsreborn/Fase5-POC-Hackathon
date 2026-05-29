# Conexao Solidaria

MVP para o desafio do hackathon da ONG Esperanca Solidaria. A solucao usa .NET 10, Swagger, JWT/RBAC, PostgreSQL, RabbitMQ, Docker Desktop, Kubernetes, Grafana, Prometheus e Zabbix.

## Arquitetura

- `Identity.Api`: cadastro de doadores, login e emissao de JWT.
- `Campaigns.Api`: gestao de campanhas, painel publico de transparencia e criacao de intencao de doacao.
- `Donations.Worker`: consumidor RabbitMQ que processa doacoes e atualiza o valor arrecadado.
- `PostgreSQL`: `identitydb`, `campaignsdb` e `zabbixdb`.
- `RabbitMQ`: fila `doacoes-recebidas` com exchange `conexao-solidaria`.
- `Grafana/Prometheus`: metricas HTTP e contadores de doacoes.
- `Zabbix`: stack pronta para monitoramento complementar.

## Pre-requisitos

- Docker Desktop com Docker Compose.
- SDK .NET 10 para desenvolvimento local.
- `kubectl`, se for usar o Kubernetes do Docker Desktop.

## Executar com Docker Compose

```powershell
docker compose up --build
```

Servicos:

- Identity Swagger: http://localhost:5001/swagger
- Campaigns Swagger: http://localhost:5002/swagger
- Worker health: http://localhost:5003/health
- RabbitMQ: http://localhost:15672 (`guest` / `guest`)
- Prometheus: http://localhost:9090
- Grafana: http://localhost:3000 (`admin` / `admin`)
- Zabbix: http://localhost:8085

Usuario gestor criado no seed:

- Email: `gestor@conexaosolidaria.local`
- Senha: `Gestor@123456`
- Role: `GestorONG`

## Fluxo principal de demo

1. Abrir o Swagger da Identity API e fazer login do gestor.
2. Copiar o `accessToken`.
3. Abrir o Swagger da Campaigns API e clicar em `Authorize`.
4. Criar campanha em `POST /api/campanhas`.
5. Criar doador em `POST /api/auth/cadastro-doador`.
6. Usar o token do doador para chamar `POST /api/doacoes`.
7. Abrir RabbitMQ e observar a fila `doacoes-recebidas`.
8. Consultar `GET /api/campanhas/transparencia` e confirmar o valor arrecadado.
9. Abrir o dashboard do Grafana e verificar requisicoes HTTP/doacoes processadas.

## Executar testes

```powershell
dotnet test ConexaoSolidaria.slnx
```

## Kubernetes no Docker Desktop

Ative o Kubernetes nas configuracoes do Docker Desktop e use:

```powershell
kubectl config use-context docker-desktop

docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile -t conexao-solidaria/identity-api:local .
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile -t conexao-solidaria/campaigns-api:local .
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile -t conexao-solidaria/donations-worker:local .

kubectl apply -f infra/k8s/conexao-solidaria.yaml
kubectl get pods -n conexao-solidaria
kubectl get svc -n conexao-solidaria
```

URLs no Kubernetes local:

- Identity Swagger: http://localhost:30081/swagger
- Campaigns Swagger: http://localhost:30082/swagger
- RabbitMQ: http://localhost:31672
- Prometheus: http://localhost:30090
- Grafana: http://localhost:30300
- Zabbix: http://localhost:30085

## Documentacao

- Diagrama: [docs/arquitetura.md](docs/arquitetura.md)
- Justificativa dos bancos: [docs/justificativa-bancos.md](docs/justificativa-bancos.md)
- Template do relatorio final: [docs/relatorio-entrega-template.md](docs/relatorio-entrega-template.md)

## CI/CD

O workflow em `.github/workflows/ci.yml` executa a cada push/pull request para `main` ou `master`:

- `dotnet restore`
- `dotnet build`
- `dotnet test`
- Docker build das tres imagens

Gestor:
eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJjZmJlOWE0OS0xN2Y0LTRhNTItYWQ3NS03MGM0YjA4ZjBjMmQiLCJlbWFpbCI6Imdlc3RvckBjb25leGFvc29saWRhcmlhLmxvY2FsIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZWlkZW50aWZpZXIiOiJjZmJlOWE0OS0xN2Y0LTRhNTItYWQ3NS03MGM0YjA4ZjBjMmQiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiR2VzdG9yIE9ORyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL2VtYWlsYWRkcmVzcyI6Imdlc3RvckBjb25leGFvc29saWRhcmlhLmxvY2FsIiwiaHR0cDovL3NjaGVtYXMubWljcm9zb2Z0LmNvbS93cy8yMDA4LzA2L2lkZW50aXR5L2NsYWltcy9yb2xlIjoiR2VzdG9yT05HIiwiZXhwIjoxNzgwMDE5MzY0LCJpc3MiOiJDb25leGFvU29saWRhcmlhIiwiYXVkIjoiQ29uZXhhb1NvbGlkYXJpYSJ9.E5hmn9SVtb0hr4gycFH2TpdKaxSqNrO73F-G_rgrkGQ

Doador:
eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI3NTk1ZmZlNi05NzMzLTQ0MzktYTEzNi1jZjFhNWQ3NGJkNjMiLCJlbWFpbCI6ImZhcmlhc0BmYXJpYXMuY29tIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZWlkZW50aWZpZXIiOiI3NTk1ZmZlNi05NzMzLTQ0MzktYTEzNi1jZjFhNWQ3NGJkNjMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiTHVjYXMgRmFyaWFzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvZW1haWxhZGRyZXNzIjoiZmFyaWFzQGZhcmlhcy5jb20iLCJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL3JvbGUiOiJEb2Fkb3IiLCJleHAiOjE3ODAwMTkyMTUsImlzcyI6IkNvbmV4YW9Tb2xpZGFyaWEiLCJhdWQiOiJDb25leGFvU29saWRhcmlhIn0.Tl4vDRVP6Hcq9HaLK9vVx9IW6xU_PCbtSFOerAYItco