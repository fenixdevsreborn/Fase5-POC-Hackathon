# Kubernetes (Kustomize) - Conexão Solidária

Playbook para subir o **Conexão Solidária** no Kubernetes local do **Docker Desktop**.
Este é o **processo real validado ao vivo** (Docker Desktop Kubernetes **v1.36.1**,
baseado em `kind`): publicação das 5 imagens no **Docker Hub** → Secret → **Keel** →
`kubectl apply -k` → port-forwards automáticos.

> **Atalho:** `pwsh infra/k8s/up.ps1` faz tudo isso em **um comando**. As seções abaixo
> detalham o passo a passo manual equivalente (útil para entender/depurar cada etapa).

Os manifestos são **Kustomize** (base + overlay), com hardening de produção.

```text
infra/k8s/
  base/                      # Manifestos por recurso (agnósticos de ambiente)
    namespace.yaml
    postgres.yaml            # StatefulSet + PVC (volumeClaimTemplates)
    rabbitmq.yaml            # StatefulSet + PVC (volumeClaimTemplates)
    elasticsearch.yaml       # Deployment + PVC dedicado
    identity-api.yaml
    campaigns-api.yaml
    donations-worker.yaml
    gateway.yaml             # YARP (entrada das APIs)
    web.yaml                 # Blazor Server
    migrations-job.yaml      # Jobs identity-migrations / campaigns-migrations (RunMigrationsOnly)
    observability.yaml       # Prometheus + Grafana
    zabbix.yaml              # zabbix-server + zabbix-web
    ingress.yaml             # nginx: /api -> gateway, / -> web
    network-policies.yaml    # default-deny ingress + allow-list
    pdb.yaml
    hpa.yaml                 # gateway, identity-api, campaigns-api (por CPU)
    kustomization.yaml
  overlays/local/
    kustomization.yaml       # namespace, images -> Docker Hub (junonn5/...:latest), patches
    resource-patches.yaml    # NodePort (gateway 30080 / web 30088) + ES enxuto + imagePullPolicy: Always
  keel/
    keel.yaml                # Keel: auto-update dos pods quando :latest muda no registry
  secret.example.yaml        # Template do Secret (o secret.yaml real é gitignored)
  up.ps1                     # sobe tudo: (publish) + Secret + Keel + apply -k + port-forwards
  down.ps1                   # derruba a stack e encerra os port-forwards
  push-dockerhub.ps1         # build + push das 5 imagens para o Docker Hub
  smoke.ps1                  # apply + rollout + get
```

> Documento de infra detalhado: `infra/k8s/README.md`. Decisões e trade-offs:
> `docs/decisoes-arquiteturais.md` (AD-19, AD-23). Observabilidade: `ReadmeObservabilidade.md`.
> Operação de incidentes: `docs/runbook.md`.

## 1. Pré-requisitos

- Docker Desktop com **Kubernetes** habilitado (`Settings > Kubernetes > Enable Kubernetes`).
  O node do control plane é o container `desktop-control-plane` (kind).
- `kubectl`.
- Para o Ingress: **nginx ingress controller** instalado e uma entrada em `hosts`
  apontando `conexao-solidaria.local` para `127.0.0.1`. Sem ele, use o fallback NodePort.
- Para o HPA: **metrics-server** no cluster.

Selecione o contexto local antes de qualquer coisa:

```powershell
kubectl config get-contexts
kubectl config use-context docker-desktop
kubectl config current-context   # deve imprimir: docker-desktop
```

Se estiver rodando via Docker Compose, pare para evitar conflito de portas:

```powershell
docker compose down
```

> **kubeconfig com CA desatualizada (só local).** Após atualizações do Docker Desktop,
> o certificado da API pode não bater com o CA do kubeconfig e o `kubectl` falha com
> `x509: certificate signed by unknown authority`. Correção **apenas para o cluster local**:
>
> ```powershell
> kubectl config set-cluster docker-desktop --insecure-skip-tls-verify=true
> ```
>
> Nunca use `--insecure-skip-tls-verify` fora de um cluster de desenvolvimento local.

## 2. Publicar as 5 imagens no Docker Hub

O overlay `local` referencia as imagens publicadas no **Docker Hub**
(`junonn5/conexao-solidaria-<svc>:latest`, repositórios **públicos**), definidas em
`overlays/local/kustomization.yaml`. O script faz build + push das cinco de uma vez,
a partir da **raiz do repo**:

```powershell
$env:DOCKERHUB_TOKEN = "<seu_PAT_do_docker_hub>"   # PAT; nunca versionar
pwsh infra/k8s/push-dockerhub.ps1
```

O script faz `docker login -u junonn5 --password-stdin` (o token não é gravado em disco;
o Docker guarda a credencial no credential store) e depois, para cada serviço:

```powershell
docker build -f src/ConexaoSolidaria.<Svc>/Dockerfile -t junonn5/conexao-solidaria-<svc>:latest .
docker push junonn5/conexao-solidaria-<svc>:latest
```

Opções úteis: `-User <outro>` (troca a conta), `-ExtraTag v2025-07-17` (publica `:latest`
**e** uma tag versionada), `-SkipLogin` (credencial já salva).

Confira o que subiu:

```powershell
docker buildx imagetools inspect junonn5/conexao-solidaria-web:latest
```

## 3. Como as imagens chegam ao cluster (não há mais `ctr import`)

O Kubernetes do Docker Desktop roda dentro do node `kind` `desktop-control-plane`, que tem
seu **próprio** container runtime (`containerd`) — **separado** do daemon do Docker. Por isso,
uma imagem que só existe em `docker images` **não** fica visível para os pods.

Antes, a solução era exportar/importar manualmente (`docker save | ctr -n k8s.io images import`).
**Isso não é mais necessário:** as imagens vivem num registry público, então o `kubelet`
as baixa sozinho. Os Deployments do overlay usam `imagePullPolicy: Always`, garantindo que
um novo push de `:latest` seja de fato baixado.

Para inspecionar o que o node tem em cache:

```powershell
docker exec desktop-control-plane ctr -n k8s.io images ls | Select-String conexao-solidaria
```

> **Auto-update:** com o **Keel** instalado (seção 5), você nem precisa reaplicar nada após
> republicar uma imagem — ele detecta o novo digest e recria os pods. Veja a seção *Rebuild*.

## 4. Criar o Secret (fora do Git)

O Secret `conexao-solidaria-secret` **não** é versionado. Copie o template, preencha
os placeholders `<...>` e aplique. `secret.yaml` está no `.gitignore` - nunca o commite.

```powershell
Copy-Item infra/k8s/secret.example.yaml infra/k8s/secret.yaml
# edite infra/k8s/secret.yaml preenchendo:
#   postgres-password, jwt-secret (>= 64 chars), rabbitmq-user/password,
#   zabbix-user/password, grafana-admin-user/password, seed-gestor-password
kubectl apply -f infra/k8s/secret.yaml
```

## 5. Deploy (Kustomize) + Keel

Validar o YAML renderizado sem aplicar:

```powershell
kubectl kustomize infra/k8s/overlays/local
```

Instalar o **Keel** (auto-update das imagens) e aplicar a stack completa:

```powershell
kubectl apply -f infra/k8s/keel/keel.yaml       # idempotente
kubectl apply -k infra/k8s/overlays/local
```

Um único `apply -k` cria **tudo ao mesmo tempo** (o Kustomize não ordena a aplicação):
os Jobs de migração `identity-migrations` / `campaigns-migrations` e os Deployments nascem
juntos. A ordem correta é garantida em runtime pelo desenho de migrações abaixo.

### Keel: auto-update quando sai imagem nova

O Keel roda no namespace `keel` e observa (poll) o registry das imagens dos Deployments
anotados. Os 5 Deployments de aplicação carregam, em `metadata.annotations`:

```yaml
keel.sh/policy: force          # tag mutável (:latest): atualiza quando o digest muda
keel.sh/trigger: poll
keel.sh/pollSchedule: "@every 1m"
```

Assim, ao rodar `push-dockerhub.ps1` de novo, o digest de `:latest` muda no Docker Hub, o
Keel detecta em ~1 min e recria os pods **sozinho** — sem `apply`, sem `rollout restart`,
sem `ctr import`. Como os repositórios são públicos, não é preciso `imagePullSecret`.

```powershell
kubectl get pods -n keel                 # keel Running
kubectl logs  -n keel deploy/keel -f     # mostra as atualizações detectadas
```

## 6. Migrações e ordem de subida

O schema é criado por **EF Migrations** (não `EnsureCreated`), aplicado por **Jobs
dedicados**, e não pelos apps no boot:

- **Jobs `identity-migrations` e `campaigns-migrations`** (`infra/k8s/base/migrations-job.yaml`)
  rodam a app com `RunMigrationsOnly=true`: aplicam as migrations pendentes e encerram com
  exit 0 (não servem HTTP, não consomem fila). Um único Job de campaigns cobre o schema do
  `campaignsdb` compartilhado por Campaigns.Api e Donations.Worker (mesmo `CampaignsDbContext`,
  AD-23).
- **Deployments** (`identity-api`, `campaigns-api`, `donations-worker`) sobem com
  `Migrations__RunOnStartup=false`: **não** migram no boot (evita a corrida de `MigrateAsync`
  entre réplicas) e apenas **aguardam** o schema já existir antes de servir tráfego.

Para garantir a ordem, aplique os Jobs primeiro e aguarde a conclusão, depois a stack:

```powershell
kubectl apply -f infra/k8s/base/migrations-job.yaml -n conexao-solidaria
kubectl wait --for=condition=complete --timeout=180s `
  job/identity-migrations job/campaigns-migrations -n conexao-solidaria
kubectl apply -k infra/k8s/overlays/local
```

Os Jobs são **idempotentes** (EF só aplica migrations pendentes) e têm
`ttlSecondsAfterFinished: 300` (somem 5 min após concluir); reaplicar a stack é seguro.

### Resultado observado (deploy validado ao vivo)

Após a publicação das imagens + Secret + Keel + `apply -k` no Docker Desktop k8s v1.36.1:

- **12 pods Running 1/1**: `postgres`, `rabbitmq`, `elasticsearch`, `identity-api`,
  `campaigns-api`, `donations-worker`, `gateway`, `web`, `prometheus`, `grafana`,
  `zabbix-server`, `zabbix-web`.
- Jobs de migração em **`Complete`** (`identity-migrations`, `campaigns-migrations`).
- E2E de doação processada **em ~3s** (POST 202 → outbox → fila `doacoes-recebidas` →
  Worker → `campaigns.ValorTotalArrecadado` + read model `campaign_stats`).
- Read model `campaign_stats` **populado**; consumer de notificações do Web **conectado**
  ao fanout `conexao-solidaria.notifications` (tempo real via SignalR).

```powershell
kubectl get pods,svc -n conexao-solidaria
kubectl get jobs -n conexao-solidaria
```

### Alternativa: smoke test automatizado

`infra/k8s/smoke.ps1` valida o overlay, checa a existência do Secret, faz `apply -k`,
aguarda o rollout de todos os StatefulSets/Deployments e imprime pods e services:

```powershell
pwsh infra/k8s/smoke.ps1
```

## 7. Troubleshooting do deploy (achados reais)

Além do troubleshooting genérico (seção 12), estes três pontos foram **encontrados e
tratados** durante o deploy ao vivo:

### (a) NetworkPolicy tem de liberar os Jobs de migração → Postgres

Com o `default-deny-ingress`, o Postgres só aceita quem estiver na allow-list. Os Jobs de
migração usam os labels `app=identity-migrations` e `app=campaigns-migrations` (**diferentes**
dos deployments); sem incluí-los, os Jobs **não** conectam ao Postgres e o **schema não
nasce** - os deployments então entram em espera/CrashLoop indefinidamente.

**Já corrigido no repo:** a policy `allow-to-postgres`
(`infra/k8s/base/network-policies.yaml`) inclui `app=identity-migrations` e
`app=campaigns-migrations` na porta 5432. Se os Jobs travarem conectando ao banco, confirme
esses seletores:

```powershell
kubectl logs -n conexao-solidaria job/campaigns-migrations
kubectl describe networkpolicy allow-to-postgres -n conexao-solidaria
```

### (b) Loop de espera de schema é curto (~30s) → risco de CrashLoop

Os apps que não migram (Worker, e as APIs) esperam o schema ficar pronto num loop de
**10 tentativas × 3s (~30s)** (ex.: `WorkerDatabaseInitializer` consulta uma tabela real
dentro de try/catch e retenta enquanto o schema não existe). Se o Postgres ainda estiver
inicializando o volume, ou o Job de migração demorar mais que isso, o app esgota as
tentativas e pode entrar em **CrashLoopBackOff**.

**Mitigação imediata (local):** depois que os Jobs de migração estiverem `Complete`,
reinicie os deployments para que reentrem no loop com o schema já pronto:

```powershell
kubectl wait --for=condition=complete --timeout=180s `
  job/identity-migrations job/campaigns-migrations -n conexao-solidaria
kubectl rollout restart deployment/identity-api deployment/campaigns-api deployment/donations-worker -n conexao-solidaria
kubectl rollout status  deployment/campaigns-api -n conexao-solidaria
```

**Recomendação para produção:** substituir o loop por um **initContainer** que bloqueia o
start do app até a migração concluir (ex.: `kubectl wait` num sidecar de bootstrap, ou um
init que sonda `__EFMigrationsHistory`), tornando a dependência explícita em vez de
best-effort com timeout. Fica como TODO (seção 11).

### (c) kubeconfig do docker-desktop com CA desatualizada

Veja a caixa na seção 1: `kubectl config set-cluster docker-desktop --insecure-skip-tls-verify=true`
(**apenas local**).

## 8. Acesso na demo

### Entrada única: Ingress (recomendado)

Todo tráfego externo passa pelo **Ingress** (`host: conexao-solidaria.local`):

| Recurso | URL |
| --- | --- |
| App (Web/Blazor) | `http://conexao-solidaria.local/` |
| API (Gateway/YARP) | `http://conexao-solidaria.local/api/...` |

Requer o nginx ingress controller e `conexao-solidaria.local -> 127.0.0.1` no `hosts`.

### Fallback NodePort (sem ingress controller)

O overlay `local` também expõe NodePort:

| Recurso | URL |
| --- | --- |
| Web | http://localhost:30088 |
| Gateway (API) | http://localhost:30080/api/... |

### port-forward (serviços internos ClusterIP)

Postgres, RabbitMQ, Elasticsearch, Prometheus, Grafana, Zabbix - e também Web/Gateway,
quando não há ingress - ficam **ClusterIP**. Acesse via `port-forward` (que **não** passa
pelas NetworkPolicies). Os Services expõem a porta **80**; os pods escutam 8080.

> **O `up.ps1` já sobe 7 desses forwards automaticamente** em segundo plano ao final do
> deploy, e eles continuam ativos depois que o script termina. Os PIDs ficam em
> `%TEMP%\conexao-solidaria-portforward.pids` e os logs em `%TEMP%\conexao-solidaria-pf-logs\`.
> Use `up.ps1 -NoForward` para não subi-los e `down.ps1` para encerrá-los.

| Serviço | URL | Automático no `up.ps1` |
|---|---|---|
| Web (Blazor) | http://localhost:18088 | sim |
| Gateway (API/YARP) | http://localhost:18080/api/... | sim |
| Swagger Identity | http://localhost:18081/swagger | sim |
| Swagger Campaigns | http://localhost:18082/swagger | sim |
| Grafana | http://localhost:3000 | sim |
| Prometheus | http://localhost:9090 | sim |
| RabbitMQ Management | http://localhost:15672 | sim |
| Zabbix Web | http://localhost:8085 | **não** (manual) |
| Elasticsearch | http://localhost:9200 | **não** (manual) |

Equivalentes manuais (ou para os dois não cobertos):

```powershell
kubectl port-forward -n conexao-solidaria svc/web           18088:80    # App (Blazor)
kubectl port-forward -n conexao-solidaria svc/gateway       18080:80    # API (YARP)
kubectl port-forward -n conexao-solidaria svc/identity-api  18081:80    # Swagger Identity
kubectl port-forward -n conexao-solidaria svc/campaigns-api 18082:80    # Swagger Campaigns
kubectl port-forward -n conexao-solidaria svc/grafana        3000:3000  # Grafana
kubectl port-forward -n conexao-solidaria svc/prometheus     9090:9090  # Prometheus
kubectl port-forward -n conexao-solidaria svc/rabbitmq      15672:15672 # RabbitMQ Management
kubectl port-forward -n conexao-solidaria svc/zabbix-web     8085:8080  # Zabbix Web
kubectl port-forward -n conexao-solidaria svc/elasticsearch  9200:9200  # Elasticsearch
```

Credenciais vêm do Secret (`grafana-admin-user/password`, `rabbitmq-user/password`,
`zabbix-user` = `Admin` + `zabbix-password`). O gestor semeado pela Identity é
`gestor@conexaosolidaria.local` com a senha `seed-gestor-password`. Nunca commite valores
reais - veja `SECURITY.md`.

## 9. Hardening aplicado

Resumo do que a base entrega (detalhes e IDs em `infra/k8s/README.md` e
`docs/decisoes-arquiteturais.md` AD-19):

- **Persistência (#K8S-003):** Postgres e RabbitMQ são `StatefulSet` com
  `volumeClaimTemplates` (PVC por réplica: Postgres 5Gi, RabbitMQ 2Gi); Elasticsearch é
  `Deployment` + PVC dedicado (3Gi, `strategy: Recreate`). Os PVCs **sobrevivem** a delete
  dos pods; `kubectl delete -k` **não** remove PVCs de `volumeClaimTemplates` (proposital).
  Prometheus/Grafana usam armazenamento efêmero (reprovisionados por ConfigMap).
- **Rede (#K8S-004/005):** **tudo ClusterIP**; entrada externa única = **Ingress nginx**
  (`/api -> gateway`, `/ -> web`). Sticky cookie de afinidade para os circuitos do Blazor
  Server. NodePort só no overlay local, como fallback.
- **Probes (#K8S-006):** `startupProbe` em `/alive` (tolera boot + espera de schema),
  `readinessProbe` em `/health` (reflete dependências), `livenessProbe` em `/alive` (**não**
  depende de DB/RabbitMQ, evita reinício em cascata). Postgres/RabbitMQ usam probes `exec`.
- **Recursos:** `requests`/`limits` em todos os pods.
- **securityContext (#K8S-008):** nos containers .NET e nos Jobs de migração: `runAsNonRoot`,
  `runAsUser 10001`, `allowPrivilegeEscalation: false`, `readOnlyRootFilesystem: true`,
  `capabilities.drop: [ALL]`, `seccompProfile: RuntimeDefault`, `emptyDir` em `/tmp` (o Web
  grava Data Protection keys em `/tmp/keys`). Imagens de infra recebem apenas `fsGroup`.
- **NetworkPolicy (#K8S-009):** `default-deny-ingress` + allow-list explícita
  (Gateway<-Web/ingress, APIs<-Gateway/Prometheus/Zabbix,
  Postgres<-APIs/Worker/**migrations**/Zabbix, RabbitMQ<-Campaigns/Worker/**Web**,
  ES<-Campaigns, Prometheus<-Grafana, etc.). Egresso permanece aberto (hardening de egress
  é TODO).
- **HPA (#K8S-010):** por CPU (70%) em `gateway`, `identity-api`, `campaigns-api`
  (min 1, max 5). Requer metrics-server.
- **PDB (#K8S-011):** `minAvailable: 1` nos stateless que escalam; `maxUnavailable: 1` nos
  de réplica única (`web`, `donations-worker`) para não bloquear drains.

## 10. Rebuild após alterar código

Republicar no Docker Hub — o **Keel** faz o resto:

```powershell
$env:DOCKERHUB_TOKEN = "<seu_PAT_do_docker_hub>"
pwsh infra/k8s/push-dockerhub.ps1
```

Em até ~1 min (`keel.sh/pollSchedule`), o Keel detecta o novo digest de `:latest` e recria
os pods. Acompanhe:

```powershell
kubectl get pods -n conexao-solidaria -w
kubectl logs -n keel deploy/keel -f
```

Se quiser forçar na hora, sem esperar o poll:

```powershell
kubectl rollout restart deployment/campaigns-api -n conexao-solidaria
kubectl rollout status  deployment/campaigns-api -n conexao-solidaria
```

> Com `imagePullPolicy: Always` + imagem no registry, o `rollout restart` já puxa a versão
> nova — não existe mais o risco de reiniciar com uma imagem velha presa no `containerd`
> do node (a antiga causa nº 1 de "meu fix não subiu").

## 11. TODO / limitações conscientes

- **initContainer de migração:** trocar o loop de espera de schema (~30s) por um
  initContainer que bloqueia o start até a migração concluir (dependência explícita).
- **KEDA:** o `donations-worker` deveria escalar pela **profundidade da fila** do RabbitMQ
  (`ScaledObject` com trigger `rabbitmq`). Hoje só há HPA por CPU nos stateless. Fica como
  TODO até o KEDA estar disponível no cluster.
- **Web em multi-réplica:** mantido em `replicas: 1`. Blazor Server usa circuitos SignalR
  (estado por conexão) e guarda o JWT via `ProtectedLocalStorage` (Data Protection).
  Escalar exige (1) Data Protection keys em volume **RWX** compartilhado
  (`DataProtection__KeysPath`) e (2) sticky sessions no Ingress (já anotado em `ingress.yaml`).
- **Hardening de egresso** nas NetworkPolicies (default-deny egress + allow-dns).

## 12. Troubleshooting geral

- **Contexto errado:** `kubectl config use-context docker-desktop`.
- **`ImagePullBackOff` / `ErrImagePull`:** o node não conseguiu baixar a imagem do Docker Hub.
  Confirme que ela existe e está pública
  (`docker buildx imagetools inspect junonn5/conexao-solidaria-<svc>:latest`); se faltar,
  rode `pwsh infra/k8s/push-dockerhub.ps1`. Cheque também conectividade e o nome/tag no
  bloco `images:` de `overlays/local/kustomization.yaml`.
- **Pods não atualizam após um push:** veja `kubectl logs -n keel deploy/keel`; confirme as
  anotações `keel.sh/*` no Deployment (`kubectl describe deploy/<nome> -n conexao-solidaria`).
  Para forçar: `kubectl rollout restart deployment/<nome> -n conexao-solidaria`.
- **Pod em CrashLoopBackOff:** `kubectl logs deployment/<nome> -n conexao-solidaria` e
  `kubectl describe pod <pod> -n conexao-solidaria`. Se for API/Worker no boot, quase sempre
  é espera de schema (seção 7b) - confirme os Jobs de migração `Complete` e faça
  `rollout restart`.
- **Jobs de migração parados/`0/1`:** veja `kubectl logs job/campaigns-migrations`; erro de
  conexão ao Postgres normalmente é NetworkPolicy (seção 7a).
- **Remover PVC antigo incompatível:** se o volume veio de uma versão criada por
  `EnsureCreated` (sem `__EFMigrationsHistory`), as migrations falham; recrie o PVC do
  Postgres (`kubectl delete pvc -n conexao-solidaria -l app=postgres`). Ver AD-11.
- **HPA sem métricas (`<unknown>`):** metrics-server não instalado.
- **Ingress não responde:** confirme o nginx ingress controller e a entrada no `hosts`;
  ou use o fallback NodePort (30088 / 30080).
- **Elasticsearch não sobe:** confira memória do Docker Desktop; o overlay local já reduz o
  ES (`ES_JAVA_OPTS=-Xms256m -Xmx256m`).

## 13. Remover

```powershell
kubectl delete -k infra/k8s/overlays/local
```

Os PVCs de Postgres/RabbitMQ **não** são removidos por padrão (evita perda acidental).
Para apagar dados e o namespace:

```powershell
kubectl delete pvc -n conexao-solidaria --all
kubectl delete namespace conexao-solidaria
```
