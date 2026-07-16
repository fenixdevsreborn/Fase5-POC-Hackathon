# Kubernetes - Conexao Solidaria

Manifestos reestruturados em **Kustomize** (base + overlays), com hardening de
producao (persistencia, ClusterIP + Ingress, probes, securityContext,
NetworkPolicy, HPA, PDB). Destino de execucao local: Kubernetes do Docker Desktop.

## Estrutura

```
infra/k8s/
  base/                      # Manifestos por recurso (agnosticos de ambiente)
    namespace.yaml
    postgres.yaml            # StatefulSet + PVC (volumeClaimTemplates)
    rabbitmq.yaml            # StatefulSet + PVC (volumeClaimTemplates)
    elasticsearch.yaml       # Deployment + PVC dedicado
    identity-api.yaml
    campaigns-api.yaml
    donations-worker.yaml
    gateway.yaml             # YARP (ponto de entrada das APIs)
    web.yaml                 # Blazor Server
    observability.yaml       # Prometheus + Grafana
    zabbix.yaml              # zabbix-server + zabbix-web
    ingress.yaml             # nginx: /api -> gateway, / -> web
    network-policies.yaml    # default-deny ingress + allow-list
    pdb.yaml
    hpa.yaml                 # gateway, identity-api, campaigns-api (por CPU)
    kustomization.yaml
  overlays/
    local/
      kustomization.yaml     # namespace, images :local, patches
      resource-patches.yaml  # NodePort (gateway/web) + ES enxuto p/ dev
  secret.example.yaml        # Template do Secret (NAO versionar o real)
  smoke.ps1                  # Aplica + aguarda rollout + checa pods
```

## Subir tudo com 1 comando (recomendado)

O script `up.ps1` executa o fluxo completo **incluindo o Secret** (gerado a partir do
`.env` da raiz do repo): contexto -> build das 5 imagens (na raiz) -> import no node kind
-> namespace + Secret -> `apply -k` -> espera migracoes -> reinicia as APIs -> reporta pods/URLs.

```powershell
pwsh infra/k8s/up.ps1              # build + deploy completo
pwsh infra/k8s/up.ps1 -SkipBuild   # redeploy sem reconstruir as imagens

pwsh infra/k8s/down.ps1            # derruba (preserva PVCs)
pwsh infra/k8s/down.ps1 -PurgeData # derruba e apaga PVCs + namespace
```

Pre-requisitos: Kubernetes habilitado no Docker Desktop, `kubectl` no PATH, `.env`
preenchido na raiz (ver `.env.example`) e Docker Compose parado (`docker compose down`).

## Passo a passo manual (Docker Desktop)

```powershell
kubectl config use-context docker-desktop

# 1) Build das imagens (contexto na raiz do repo)
docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile   -t conexao-solidaria/identity-api:local .
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile  -t conexao-solidaria/campaigns-api:local .
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile -t conexao-solidaria/donations-worker:local .
docker build -f src/ConexaoSolidaria.Gateway/Dockerfile        -t conexao-solidaria/gateway:local .
docker build -f src/ConexaoSolidaria.Web/Dockerfile            -t conexao-solidaria/web:local .

# 2) Secret (fora do Git). Preencha os placeholders antes de aplicar.
cp infra/k8s/secret.example.yaml infra/k8s/secret.yaml
#   edite infra/k8s/secret.yaml
kubectl apply -f infra/k8s/secret.yaml

# 3) Deploy (Kustomize)
kubectl apply -k infra/k8s/overlays/local

# 4) (Opcional) smoke test automatizado
pwsh infra/k8s/smoke.ps1
```

Validacao sem aplicar (renderiza o YAML final):

```powershell
kubectl kustomize infra/k8s/overlays/local
```

## Acesso na demo

O unico ponto de entrada externo e o **Ingress** (`host: conexao-solidaria.local`):

- App (Web/Blazor): `http://conexao-solidaria.local/`
- API (Gateway/YARP): `http://conexao-solidaria.local/api/...`

Requer o **nginx ingress controller** instalado e uma entrada em `hosts`
apontando `conexao-solidaria.local` para `127.0.0.1`.

Sem ingress controller, o overlay `local` tambem expoe **NodePort** como fallback:

- Web: http://localhost:30088
- Gateway (API): http://localhost:30080/api/...

Os servicos internos ficam **ClusterIP** e sao acessados na demo via
`kubectl port-forward` (o port-forward nao passa por NetworkPolicy):

```powershell
kubectl port-forward -n conexao-solidaria svc/grafana       3000:3000   # Grafana
kubectl port-forward -n conexao-solidaria svc/prometheus     9090:9090   # Prometheus
kubectl port-forward -n conexao-solidaria svc/rabbitmq      15672:15672  # RabbitMQ management
kubectl port-forward -n conexao-solidaria svc/zabbix-web     8085:8080   # Zabbix
kubectl port-forward -n conexao-solidaria svc/elasticsearch  9200:9200   # Elasticsearch
```

## Decisoes de arquitetura

### Persistencia (#K8S-003)
- **PostgreSQL** e **RabbitMQ**: `StatefulSet` com `volumeClaimTemplates` (PVC
  dedicado por replica). Postgres 5Gi (`PGDATA` em subdiretorio para evitar
  conflito com `lost+found`); RabbitMQ 2Gi.
- **Elasticsearch**: `Deployment` + `PersistentVolumeClaim` dedicado (3Gi),
  `strategy: Recreate` (RWO, um pod por vez).
- **Retencao**: os PVCs sobrevivem a delete/recreate dos pods. `kubectl delete -k`
  NAO remove PVCs gerados por `volumeClaimTemplates` (precisam ser removidos
  manualmente: `kubectl delete pvc -n conexao-solidaria --all`) - isso e
  proposital para nao perder dados por engano. Prometheus/Grafana usam
  armazenamento efemero (metricas/dashboards reprovisionados via ConfigMap).

### Tipos de Service (#K8S-004 / #K8S-005)
- **Tudo ClusterIP** (postgres, rabbitmq, elasticsearch, identity-api,
  campaigns-api, donations-worker, gateway, web, prometheus, grafana, zabbix).
- Ponto de entrada externo unico = **Ingress** (nginx). NodePort so no overlay
  local como fallback de desenvolvimento.
- As APIs e o Gateway expoem `port: 80 -> targetPort: 8080`. Isso e necessario
  para o **service discovery** do YARP/`Microsoft.Extensions.ServiceDiscovery`:
  `http://identity-api` resolve por DNS do Service e usa a porta http default 80.
  Os nomes dos Services (`identity-api`, `campaigns-api`, `gateway`) batem
  exatamente com os hosts logicos usados no codigo (appsettings do Gateway e
  `BaseAddress` do Web).

### Probes (#K8S-006)
- APIs/Gateway/Web: `startupProbe` em `/alive` (`failureThreshold: 30`, tolera o
  boot + EF Migrate), `readinessProbe` em `/health` (pode refletir dependencias),
  `livenessProbe` em `/alive` (NAO depende de DB/RabbitMQ - evita reinicio em
  cascata). Endpoints vem do `ServiceDefaults` (`MapDefaultEndpoints`).
- Worker: mesmas probes (ele sobe um host HTTP com `/health`, `/alive`, `/metrics`).
- Postgres/RabbitMQ: probes `exec` (`pg_isready`, `rabbitmq-diagnostics`).

### securityContext (#K8S-008)
- Containers .NET (gateway, web, identity, campaigns, worker):
  `runAsNonRoot: true`, `runAsUser: 10001`, `allowPrivilegeEscalation: false`,
  `readOnlyRootFilesystem: true`, `capabilities.drop: [ALL]`,
  `seccompProfile: RuntimeDefault`. `emptyDir` montado em `/tmp` (exigido pelo
  rootfs somente-leitura); o Web grava as Data Protection keys em `/tmp/keys`.
- Imagens de infra (postgres/rabbitmq/elasticsearch) precisam de uids proprios e
  init como root; recebem apenas `fsGroup` para o PVC gravavel (nao forcamos
  `runAsNonRoot`/`readOnly`, que quebrariam essas imagens).

### NetworkPolicy (#K8S-009)
- `default-deny-ingress` no namespace + allow-list explicito: Gateway<-Web/ingress,
  Web<-ingress, APIs<-Gateway, Postgres<-APIs/Worker/Zabbix, RabbitMQ<-Campaigns/Worker,
  Elasticsearch<-Campaigns, Prometheus/Zabbix->/metrics das APIs e Worker,
  Prometheus<-Grafana, zabbix-server<-zabbix-web.
- Egresso permanece aberto (DNS funciona sem regra extra). Hardening de egresso =
  **TODO** (exigiria `allow-dns` + regras por servico).

### HPA (#K8S-010) e PDB (#K8S-011)
- HPA por CPU (70%) em `gateway`, `identity-api`, `campaigns-api` (min 1, max 5).
  Requer **metrics-server** no cluster.
- PDB: `minAvailable: 1` para os stateless que escalam (gateway/identity/campaigns);
  `maxUnavailable: 1` para replica unica (web/worker) para nao bloquear drains.

## TODO / Limitacoes conscientes
- **KEDA**: o `donations-worker` deveria escalar pelo tamanho da fila do RabbitMQ
  (`ScaledObject` com trigger `rabbitmq`). Fica como TODO ate o KEDA no cluster.
- **Hardening de egresso** nas NetworkPolicies (default-deny egress + allow-dns).
- **Web em multi-replica**: mantido em `replicas: 1`. Blazor Server usa circuitos
  SignalR (estado por conexao) e guarda o JWT via `ProtectedLocalStorage`
  (Data Protection). Escalar exige (1) Data Protection keys em volume `RWX`
  compartilhado (`DataProtection__KeysPath`) e (2) sticky sessions no Ingress
  (ja anotado em `ingress.yaml`: `affinity: cookie`).
