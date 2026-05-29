# Kubernetes local no Docker Desktop

Use este manifesto com o Kubernetes habilitado no Docker Desktop.

```powershell
kubectl config use-context docker-desktop

docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile -t conexao-solidaria/identity-api:local .
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile -t conexao-solidaria/campaigns-api:local .
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile -t conexao-solidaria/donations-worker:local .

kubectl apply -f infra/k8s/conexao-solidaria.yaml
kubectl get pods -n conexao-solidaria
kubectl get svc -n conexao-solidaria
```

URLs NodePort no Docker Desktop:

- Identity Swagger: http://localhost:30081/swagger
- Campaigns Swagger: http://localhost:30082/swagger
- Worker health: http://localhost:30083/health
- RabbitMQ: http://localhost:31672
- Prometheus: http://localhost:30090
- Grafana: http://localhost:30300
- Zabbix: http://localhost:30085
