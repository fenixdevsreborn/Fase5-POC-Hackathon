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
      kustomization.yaml     # namespace, images -> Docker Hub (junonn5/...:latest), patches
      resource-patches.yaml  # NodePort (gateway/web) + ES enxuto + imagePullPolicy: Always
  keel/
    keel.yaml                # Keel: auto-update dos pods quando :latest muda no registry
  secret.example.yaml        # Template do Secret (NAO versionar o real)
  up.ps1                     # Sobe tudo: (publish) + Secret + Keel + apply -k + port-forwards
  down.ps1                   # Derruba a stack e encerra os port-forwards
  push-dockerhub.ps1         # Build + push das 5 imagens para o Docker Hub
  smoke.ps1                  # Aplica + aguarda rollout + checa pods
```

## Imagens: Docker Hub + auto-update (Keel)

As 5 imagens da aplicacao sao publicadas no **Docker Hub** como
`junonn5/conexao-solidaria-<svc>:latest` (repositorios **publicos**). O node baixa direto
do registry — **nao ha mais** `docker save | ctr images import`.

```powershell
$env:DOCKERHUB_TOKEN = "<PAT>"        # token via env; nunca versionado
pwsh infra/k8s/push-dockerhub.ps1     # build + push das 5 imagens
```

O **Keel** (`keel/keel.yaml`, namespace `keel`) observa essas tags. Os 5 Deployments tem
`keel.sh/policy: force`, `keel.sh/trigger: poll` e `keel.sh/pollSchedule: "@every 1m"`:
ao republicar, o digest de `:latest` muda e o Keel recria os pods sozinho em ~1 min.
Com `imagePullPolicy: Always`, o pull traz de fato a versao nova.

```powershell
kubectl logs -n keel deploy/keel -f   # acompanha as atualizacoes detectadas
```

## Subir tudo com 1 comando (recomendado)

O script `up.ps1` executa o fluxo completo **incluindo o Secret** (gerado a partir do
`.env` da raiz do repo): contexto -> (opcional) publish no Docker Hub -> namespace + Secret
-> instala o Keel -> `apply -k` -> espera migracoes -> reinicia as APIs -> reporta pods/URLs
-> **sobe os port-forwards em segundo plano**.

```powershell
pwsh infra/k8s/up.ps1              # deploy puxando as imagens do Docker Hub
pwsh infra/k8s/up.ps1 -Publish     # publica no Docker Hub antes de subir (usa DOCKERHUB_TOKEN)
pwsh infra/k8s/up.ps1 -NoForward   # nao inicia os port-forwards automaticos
                                   # (-SkipBuild ainda e aceito, mas virou no-op)

pwsh infra/k8s/down.ps1            # derruba (preserva PVCs) e encerra os port-forwards
pwsh infra/k8s/down.ps1 -PurgeData # derruba e apaga PVCs + namespace
```

Pre-requisitos: Kubernetes habilitado no Docker Desktop, `kubectl` no PATH, `.env`
preenchido na raiz (ver `.env.example`), imagens ja publicadas no Docker Hub (ou use
`-Publish`) e Docker Compose parado (`docker compose down`).

## Passo a passo manual (Docker Desktop)

```powershell
kubectl config use-context docker-desktop

# 1) Publicar as imagens no Docker Hub (contexto de build na raiz do repo)
$env:DOCKERHUB_TOKEN = "<PAT>"
pwsh infra/k8s/push-dockerhub.ps1

# 2) Secret (fora do Git). Preencha os placeholders antes de aplicar.
cp infra/k8s/secret.example.yaml infra/k8s/secret.yaml
#   edite infra/k8s/secret.yaml
kubectl apply -f infra/k8s/secret.yaml

# 3) Keel (auto-update) + Deploy (Kustomize)
kubectl apply -f infra/k8s/keel/keel.yaml
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
`kubectl port-forward` (o port-forward nao passa por NetworkPolicy, entao a demo
funciona mesmo com o `default-deny-ingress`).

> **O `up.ps1` ja sobe 7 destes forwards automaticamente** (web, gateway, identity-api,
> campaigns-api, grafana, prometheus, rabbitmq) em segundo plano, e eles seguem ativos
> apos o script terminar. PIDs em `%TEMP%\conexao-solidaria-portforward.pids`, logs em
> `%TEMP%\conexao-solidaria-pf-logs\`. Desative com `-NoForward`; encerre com `down.ps1`.
> **Zabbix e Elasticsearch nao entram na lista automatica** — rode-os manualmente.

```powershell
# App e API (uso geral / Postman)
kubectl port-forward -n conexao-solidaria svc/web           18088:80    # App (Blazor): http://localhost:18088
kubectl port-forward -n conexao-solidaria svc/gateway       18080:80    # API (Postman): http://localhost:18080/api/...

# Observabilidade
kubectl port-forward -n conexao-solidaria svc/grafana        3000:3000  # Grafana (metricas/dashboards): http://localhost:3000
kubectl port-forward -n conexao-solidaria svc/prometheus     9090:9090  # Prometheus (targets/PromQL):   http://localhost:9090
kubectl port-forward -n conexao-solidaria svc/rabbitmq      15672:15672 # RabbitMQ management
kubectl port-forward -n conexao-solidaria svc/zabbix-web     8085:8080  # Zabbix
kubectl port-forward -n conexao-solidaria svc/elasticsearch  9200:9200  # Elasticsearch

# Swagger (ja habilitado nas duas APIs; o gateway so roteia /api/*, entao acesse a API direto)
kubectl port-forward -n conexao-solidaria svc/identity-api  18081:80    # Swagger Identity:  http://localhost:18081/swagger
kubectl port-forward -n conexao-solidaria svc/campaigns-api 18082:80    # Swagger Campaigns: http://localhost:18082/swagger
```

### Credenciais padrao

Os valores reais vem do `.env` da raiz (via Secret `conexao-solidaria-secret`); abaixo
os defaults do template.

| Acesso | Usuario | Senha | Origem |
|--------|---------|-------|--------|
| **Grafana** (http://localhost:3000) | `admin` | `GRAFANA_ADMIN_PASSWORD` | `.env` -> Secret `grafana-admin-*` |
| **API - gestor/admin** (login) | `gestor@conexaosolidaria.local` | `SEED_MANAGER_PASSWORD` | `.env` -> Secret `seed-gestor-password` (env `Seed__Gestor__Senha`) |

- O gestor tem role `GestorOng` (cria/gerencia campanhas). Ele e semeado no boot da
  Identity API **somente se** `SEED_MANAGER_PASSWORD` estiver definido no `.env`.
- E-mail/CPF/nome do gestor usam os defaults (`gestor@conexaosolidaria.local`, CPF
  `52998224725`, "Gestor ONG"); para mudar, defina `Seed__Gestor__Email` etc.
- Para atuar como **doador**, crie um usuario via `POST /api/auth/cadastro-doador`
  (a senha e a que voce enviar no corpo).

### Teste no Postman (via gateway)

Com o port-forward `svc/gateway 18080:80`, o ponto de entrada e
`http://localhost:18080/api/...` (o YARP roteia apenas `/api/*`):

| Metodo | Endpoint | Auth | Descricao |
|--------|----------|------|-----------|
| POST | `/api/auth/login` | publico | autentica (gestor ou doador) e retorna o JWT |
| POST | `/api/auth/cadastro-doador` | publico | cria um doador e retorna o JWT |
| GET  | `/api/auth/me` | Bearer | dados do usuario logado |
| GET  | `/api/campanhas/search` | publico | lista/busca campanhas |
| POST | `/api/campanhas` | Bearer (gestor) | cria campanha |
| POST | `/api/doacoes` | Bearer (doador) | registra doacao |

Fluxo: faca `POST /api/auth/login` com `{ "email": "gestor@conexaosolidaria.local",
"senha": "<SEED_MANAGER_PASSWORD>" }`, copie o `accessToken` da resposta e use nas
rotas protegidas no header `Authorization: Bearer {accessToken}`.

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
