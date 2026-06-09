# Kubernetes no Docker Desktop

Este guia mostra o passo a passo para subir a aplicacao Conexao Solidaria no Kubernetes local do Docker Desktop.

O manifesto principal fica em:

```text
infra/k8s/conexao-solidaria.yaml
```

Ele cria:

- Namespace `conexao-solidaria`.
- PostgreSQL.
- RabbitMQ com Management UI.
- Elasticsearch para busca de campanhas.
- Identity API.
- Campaigns API.
- Donations Worker.
- Prometheus.
- Grafana.
- Zabbix Server.
- Zabbix Web.

## 1. Pre-requisitos

Instale e habilite:

- Docker Desktop.
- Kubernetes do Docker Desktop.
- `kubectl`.

No Docker Desktop:

1. Abra `Settings`.
2. Va em `Kubernetes`.
3. Marque `Enable Kubernetes`.
4. Clique em `Apply & Restart`.
5. Aguarde o status do Kubernetes ficar ativo.

Verifique no terminal:

```powershell
docker --version
kubectl version --client
```

## 2. Selecionar o contexto correto

Antes de aplicar qualquer manifesto, selecione o contexto local do Docker Desktop:

```powershell
kubectl config get-contexts
kubectl config use-context docker-desktop
kubectl config current-context
```

O resultado esperado para o ultimo comando:

```text
docker-desktop
```

Se o contexto estiver apontando para outro cluster, como EKS, AKS ou GKE, o deploy pode falhar ou ir para o ambiente errado.

## 3. Opcional: parar o Docker Compose

Se a aplicacao estiver rodando via Docker Compose, pare antes de subir no Kubernetes para evitar confusao durante os testes:

```powershell
docker compose down
```

## 4. Build das imagens locais

O manifesto Kubernetes usa imagens locais com tag `:local`:

```text
conexao-solidaria/identity-api:local
conexao-solidaria/campaigns-api:local
conexao-solidaria/donations-worker:local
```

Crie as imagens:

```powershell
docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile -t conexao-solidaria/identity-api:local .
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile -t conexao-solidaria/campaigns-api:local .
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile -t conexao-solidaria/donations-worker:local .
```

Confira se as imagens foram criadas:

```powershell
docker images conexao-solidaria/identity-api
docker images conexao-solidaria/campaigns-api
docker images conexao-solidaria/donations-worker
```

## 5. Aplicar o manifesto Kubernetes

Suba todos os recursos:

```powershell
kubectl apply -f infra/k8s/conexao-solidaria.yaml
```

Confira o namespace:

```powershell
kubectl get namespace conexao-solidaria
```

Confira os pods:

```powershell
kubectl get pods -n conexao-solidaria
```

Confira os services:

```powershell
kubectl get svc -n conexao-solidaria
```

## 6. Aguardar os pods ficarem prontos

Use:

```powershell
kubectl wait --for=condition=Ready pod --all -n conexao-solidaria --timeout=300s
```

Se algum pod ainda estiver criando banco, baixando imagem ou iniciando a aplicacao, acompanhe com:

```powershell
kubectl get pods -n conexao-solidaria -w
```

## 7. URLs de acesso

No Docker Desktop, os services `NodePort` ficam acessiveis via `localhost`.

| Recurso | URL |
| --- | --- |
| Identity Swagger | http://localhost:30081/swagger |
| Campaigns Swagger | http://localhost:30082/swagger |
| Donations Worker health | http://localhost:30083/health |
| Elasticsearch | http://localhost:30920 |
| RabbitMQ Management | http://localhost:31672 |
| Prometheus | http://localhost:30090 |
| Grafana | http://localhost:30300 |
| Zabbix Web | http://localhost:30085 |

Credenciais:

| Recurso | Usuario | Senha |
| --- | --- | --- |
| RabbitMQ | `guest` | `guest` |
| Grafana | `admin` | `admin` |
| Zabbix | `Admin` | `zabbix` |

Usuario gestor seedado pela Identity API:

| Campo | Valor |
| --- | --- |
| Email | `gestor@conexaosolidaria.local` |
| Senha | `Gestor@123456` |
| Role | `GestorONG` |

## 8. Teste rapido da aplicacao

### 8.1. Login do gestor

Acesse:

```text
http://localhost:30081/swagger
```

Use o endpoint:

```text
POST /api/auth/login
```

Payload:

```json
{
  "email": "gestor@conexaosolidaria.local",
  "senha": "Gestor@123456"
}
```

Copie o campo `accessToken`.

### 8.2. Criar campanha

Acesse:

```text
http://localhost:30082/swagger
```

Clique em `Authorize` e informe:

```text
Bearer SEU_TOKEN
```

Use:

```text
POST /api/campanhas
```

Payload de exemplo:

```json
{
  "titulo": "Natal Solidario",
  "descricao": "Campanha para arrecadacao de brinquedos e alimentos.",
  "dataInicio": "2026-06-01T00:00:00Z",
  "dataFim": "2026-12-31T23:59:59Z",
  "metaFinanceira": 10000,
  "status": "Ativa"
}
```

### 8.3. Criar doador

Volte para:

```text
http://localhost:30081/swagger
```

Use:

```text
POST /api/auth/cadastro-doador
```

Payload:

```json
{
  "nomeCompleto": "Maria Doadora",
  "email": "maria.doadora@example.com",
  "cpf": "390.533.447-05",
  "senha": "Doador@123"
}
```

Copie o `accessToken` retornado para usar como doador.

### 8.4. Fazer doacao

No Swagger da Campaigns API:

```text
http://localhost:30082/swagger
```

Autorize com o token do doador:

```text
Bearer TOKEN_DO_DOADOR
```

Use:

```text
POST /api/doacoes
```

Payload:

```json
{
  "idCampanha": "ID_DA_CAMPANHA",
  "valorDoacao": 150
}
```

Depois consulte:

```text
GET /api/campanhas/transparencia
```

O valor arrecadado deve ser atualizado pelo `Donations Worker`.

### 8.5. Buscar campanhas no Elasticsearch

Depois de criar uma campanha, consulte:

```text
GET /api/campanhas/search?q=Natal&page=1&pageSize=10
```

O endpoint faz busca fuzzy em `titulo` e `descricao` e retorna os metadados de paginacao.

Para verificar o Elasticsearch diretamente:

```powershell
curl.exe http://localhost:30920/_cluster/health
curl.exe http://localhost:30920/_cat/indices?v
```

## 9. Observabilidade no Kubernetes

Prometheus:

```text
http://localhost:30090
```

Consulta para verificar targets:

```promql
up
```

Grafana:

```text
http://localhost:30300
```

Login:

```text
admin / admin
```

Abra o dashboard:

```text
Conexao Solidaria - Kubernetes
```

Metricas importantes:

```promql
sum(rate(http_requests_received_total[1m])) by (job)
sum(conexao_donations_processed_total)
sum(conexao_donations_rejected_total)
```

Zabbix:

```text
http://localhost:30085
```

Login:

```text
Admin / zabbix
```

## 10. Ver logs

Identity API:

```powershell
kubectl logs -f deployment/identity-api -n conexao-solidaria
```

Campaigns API:

```powershell
kubectl logs -f deployment/campaigns-api -n conexao-solidaria
```

Donations Worker:

```powershell
kubectl logs -f deployment/donations-worker -n conexao-solidaria
```

RabbitMQ:

```powershell
kubectl logs -f deployment/rabbitmq -n conexao-solidaria
```

Elasticsearch:

```powershell
kubectl logs -f deployment/elasticsearch -n conexao-solidaria
```

PostgreSQL:

```powershell
kubectl logs -f deployment/postgres -n conexao-solidaria
```

## 11. Rebuild apos alterar codigo

Depois de alterar codigo das APIs ou do worker, reconstrua as imagens:

```powershell
docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile -t conexao-solidaria/identity-api:local .
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile -t conexao-solidaria/campaigns-api:local .
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile -t conexao-solidaria/donations-worker:local .
```

Reinicie os deployments:

```powershell
kubectl rollout restart deployment/identity-api -n conexao-solidaria
kubectl rollout restart deployment/campaigns-api -n conexao-solidaria
kubectl rollout restart deployment/donations-worker -n conexao-solidaria
```

Acompanhe:

```powershell
kubectl rollout status deployment/identity-api -n conexao-solidaria
kubectl rollout status deployment/campaigns-api -n conexao-solidaria
kubectl rollout status deployment/donations-worker -n conexao-solidaria
```

## 12. Remover a aplicacao do Kubernetes

Para remover todos os recursos criados pelo manifesto:

```powershell
kubectl delete -f infra/k8s/conexao-solidaria.yaml
```

Confirme:

```powershell
kubectl get all -n conexao-solidaria
```

Se o namespace ainda existir e voce quiser remove-lo manualmente:

```powershell
kubectl delete namespace conexao-solidaria
```

## 13. Troubleshooting

### Docker Desktop nao esta rodando

Erro comum:

```text
dockerDesktopLinuxEngine
```

Solucao:

1. Abra o Docker Desktop.
2. Aguarde o engine iniciar.
3. Rode:

```powershell
docker ps
```

### Contexto Kubernetes errado

Se `kubectl` tentar acessar outro cluster:

```powershell
kubectl config get-contexts
kubectl config use-context docker-desktop
```

### Pod em CrashLoopBackOff

Veja os logs:

```powershell
kubectl logs deployment/NOME_DO_DEPLOYMENT -n conexao-solidaria
```

Veja os eventos:

```powershell
kubectl describe pod NOME_DO_POD -n conexao-solidaria
```

### Imagem local nao encontrada

Confirme se a imagem existe:

```powershell
docker images conexao-solidaria/identity-api
docker images conexao-solidaria/campaigns-api
docker images conexao-solidaria/donations-worker
```

Depois reinicie o deployment:

```powershell
kubectl rollout restart deployment/identity-api -n conexao-solidaria
```

### Banco ainda nao inicializou

Confira:

```powershell
kubectl logs -f deployment/postgres -n conexao-solidaria
```

As APIs tentam reconectar ao banco durante a inicializacao, entao aguarde alguns segundos e veja:

```powershell
kubectl get pods -n conexao-solidaria
```

### Grafana sem dados

1. Acesse Prometheus em http://localhost:30090.
2. Execute:

```promql
up
```

3. Gere trafego nos Swagger.
4. Aguarde o scrape de 5 segundos.
5. Atualize o dashboard no Grafana.

### RabbitMQ sem mensagens visiveis

O worker consome rapidamente as mensagens. Para demonstrar o fluxo:

1. Abra RabbitMQ em http://localhost:31672.
2. Va em `Queues and Streams`.
3. Observe a fila `doacoes-recebidas`.
4. Envie uma doacao pelo Swagger.
5. A mensagem pode aparecer e sumir rapidamente porque o worker processa a fila.

### Elasticsearch nao inicia

Confira o pod e os logs:

```powershell
kubectl get pods -n conexao-solidaria
kubectl describe deployment/elasticsearch -n conexao-solidaria
kubectl logs deployment/elasticsearch -n conexao-solidaria
```

O Elasticsearch reserva ate `1Gi` de memoria no manifesto. Confirme que o Docker Desktop tem memoria suficiente disponivel para o Kubernetes.

### Busca retorna lista vazia

Somente campanhas criadas ou atualizadas depois da implantacao da integracao sao indexadas automaticamente. Crie uma nova campanha ou atualize uma existente e consulte novamente:

```text
GET /api/campanhas/search?q=parte-do-titulo&page=1&pageSize=10
```

## 14. Observacao sobre persistencia

Este manifesto e voltado para ambiente local de hackathon.

PostgreSQL e Elasticsearch usam `emptyDir`, portanto os dados podem ser perdidos quando os pods forem recriados ou o manifesto removido. Para producao, troque por `PersistentVolumeClaim`, configure secrets reais e publique imagens em um registry.
