# =============================================================================
# up.ps1 - Sobe TODO o stack do Conexao Solidaria no Kubernetes do Docker Desktop
# com UM comando, incluindo o Secret (gerado a partir do .env da raiz do repo).
#
# Faz, em ordem (fluxo validado ao vivo - ver ReadmeKubernetes.md):
#   1. seleciona o contexto docker-desktop
#   2. build das 5 imagens :local (contexto = raiz do repo)
#   3. importa as imagens no node kind (ctr) - sem isso: ErrImageNeverPull
#   4. cria o namespace e o Secret 'conexao-solidaria-secret' a partir do .env
#   5. kubectl apply -k (postgres, rabbitmq, es, Jobs de migracao, deployments)
#   6. espera StatefulSets + Jobs de migracao e reinicia as APIs (evita CrashLoop)
#   7. aguarda o rollout e imprime pods/services + URLs de acesso
#
# Uso:
#   pwsh infra/k8s/up.ps1              # build + deploy completo
#   pwsh infra/k8s/up.ps1 -SkipBuild   # redeploy sem reconstruir as imagens
#
# Pre-requisitos:
#   - Docker Desktop com Kubernetes habilitado (node 'desktop-control-plane').
#   - kubectl no PATH.
#   - .env preenchido na raiz do repo (ver .env.example).
#   - Docker Compose parado (docker compose down) para nao conflitar portas.
# =============================================================================
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path   # ...\infra\k8s
$repoRoot  = Split-Path -Parent (Split-Path -Parent $scriptDir) # raiz do repo
$overlay   = Join-Path $scriptDir "overlays/local"
$ns        = "conexao-solidaria"
$node      = "desktop-control-plane"

# Servico logico -> Dockerfile (relativo a raiz do repo). Casa com os nomes de
# imagem em overlays/local/kustomization.yaml (conexao-solidaria/<svc>:local).
$services = [ordered]@{
    "identity-api"     = "src/ConexaoSolidaria.Identity.Api/Dockerfile"
    "campaigns-api"    = "src/ConexaoSolidaria.Campaigns.Api/Dockerfile"
    "donations-worker" = "src/ConexaoSolidaria.Donations.Worker/Dockerfile"
    "gateway"          = "src/ConexaoSolidaria.Gateway/Dockerfile"
    "web"              = "src/ConexaoSolidaria.Web/Dockerfile"
}

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- 1) Contexto ------------------------------------------------------------
Write-Step "Selecionando o contexto 'docker-desktop'..."
kubectl config use-context docker-desktop | Out-Null

# Apos updates do Docker Desktop, o CA do kubeconfig pode ficar defasado e o kubectl
# falha com 'x509: certificate signed by unknown authority'. Correcao SO PARA O
# CLUSTER LOCAL docker-desktop (nunca use isto fora de dev). Ver ReadmeKubernetes.md.
$probe = kubectl get --raw='/readyz' 2>&1
if ($LASTEXITCODE -ne 0 -and ($probe -match 'x509|certificate signed by unknown authority')) {
    Write-Step "kubeconfig com CA defasada (local); aplicando --insecure-skip-tls-verify no cluster docker-desktop..."
    kubectl config set-cluster docker-desktop --insecure-skip-tls-verify=true | Out-Null
}

# --- 2) Build das imagens (contexto = raiz do repo) -------------------------
if ($SkipBuild) {
    Write-Step "Pulando o build (-SkipBuild)."
}
else {
    Write-Step "Build das 5 imagens locais (contexto: $repoRoot)..."
    Push-Location $repoRoot
    try {
        foreach ($svc in $services.Keys) {
            $dockerfile = $services[$svc]
            $tag = "conexao-solidaria/$svc`:local"
            Write-Host "    -> $tag  ($dockerfile)" -ForegroundColor DarkGray
            docker build -f $dockerfile -t $tag .
            if ($LASTEXITCODE -ne 0) { throw "docker build falhou para $svc" }
        }
    }
    finally { Pop-Location }
}

# --- 3) Importar as imagens no node kind (passo critico) --------------------
# O k8s do Docker Desktop roda num node kind com containerd proprio, separado do
# daemon do Docker. Sem importar, os pods ficam em ErrImageNeverPull.
Write-Step "Importando as imagens no node '$node' (ctr -n k8s.io)..."
foreach ($svc in $services.Keys) {
    $tag = "conexao-solidaria/$svc`:local"
    Write-Host "    -> import $tag" -ForegroundColor DarkGray
    docker save $tag | docker exec -i $node ctr -n k8s.io images import -
    if ($LASTEXITCODE -ne 0) { throw "falha ao importar $tag no node" }
}

# --- 4) Namespace + Secret (gerado do .env) ---------------------------------
Write-Step "Aplicando o namespace..."
kubectl apply -f (Join-Path $scriptDir "base/namespace.yaml") | Out-Null

$envPath = Join-Path $repoRoot ".env"
if (-not (Test-Path $envPath)) {
    throw ".env nao encontrado em $envPath. Copie o .env.example e preencha os valores."
}

Write-Step "Gerando o Secret 'conexao-solidaria-secret' a partir do .env..."
$envVars = @{}
foreach ($line in Get-Content $envPath) {
    $t = $line.Trim()
    if ($t -eq "" -or $t.StartsWith("#")) { continue }
    $kv = $t -split "=", 2
    if ($kv.Count -eq 2) { $envVars[$kv[0].Trim()] = $kv[1].Trim() }
}

function Require-Env($name) {
    if (-not $envVars.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($envVars[$name])) {
        throw "Variavel '$name' ausente/vazia no .env."
    }
    return $envVars[$name]
}

# Mapeia .env -> chaves do Secret (ver secret.example.yaml).
# zabbix-user nao existe no .env; usa o default 'Admin' (ReadmeKubernetes secao 8).
$zabbixUser = if ($envVars.ContainsKey("ZABBIX_USER") -and $envVars["ZABBIX_USER"]) { $envVars["ZABBIX_USER"] } else { "Admin" }
$literals = @(
    "--from-literal=postgres-password=$(Require-Env 'POSTGRES_PASSWORD')"
    "--from-literal=jwt-secret=$(Require-Env 'JWT_SECRET')"
    "--from-literal=rabbitmq-user=$(Require-Env 'RABBITMQ_USER')"
    "--from-literal=rabbitmq-password=$(Require-Env 'RABBITMQ_PASSWORD')"
    "--from-literal=grafana-admin-user=$(Require-Env 'GRAFANA_ADMIN_USER')"
    "--from-literal=grafana-admin-password=$(Require-Env 'GRAFANA_ADMIN_PASSWORD')"
    "--from-literal=zabbix-user=$zabbixUser"
    "--from-literal=zabbix-password=$(Require-Env 'ZABBIX_PASSWORD')"
    "--from-literal=seed-gestor-password=$(Require-Env 'SEED_MANAGER_PASSWORD')"
)
# Idempotente: dry-run gera o YAML e o apply cria/atualiza.
kubectl create secret generic conexao-solidaria-secret -n $ns @literals --dry-run=client -o yaml | kubectl apply -f -
if ($LASTEXITCODE -ne 0) { throw "falha ao aplicar o Secret" }

# --- 5) Deploy completo (Kustomize) -----------------------------------------
Write-Step "Aplicando a stack completa (kubectl apply -k)..."
kubectl apply -k $overlay
if ($LASTEXITCODE -ne 0) { throw "kubectl apply -k falhou" }

# --- 6) Ordenacao + mitigacao do CrashLoop ----------------------------------
# O Kustomize nao ordena; postgres/rabbitmq e os Jobs de migracao sobem junto com
# os deployments. Esperamos os dados, depois as migracoes, e reiniciamos as apps
# para reentrarem no loop de espera de schema com o schema ja pronto (ver
# ReadmeKubernetes secao 7b) - evita CrashLoopBackOff dos ~30s de timeout.
Write-Step "Aguardando os StatefulSets (postgres, rabbitmq)..."
kubectl rollout status statefulset/postgres  -n $ns --timeout=300s
kubectl rollout status statefulset/rabbitmq  -n $ns --timeout=300s

Write-Step "Aguardando os Jobs de migracao (identity/campaigns)..."
kubectl wait --for=condition=complete --timeout=180s `
    job/identity-migrations job/campaigns-migrations -n $ns

Write-Step "Reiniciando as APIs/Worker (schema ja aplicado)..."
kubectl rollout restart deployment/identity-api deployment/campaigns-api deployment/donations-worker -n $ns

# --- 7) Aguardar rollout e reportar -----------------------------------------
$deployments = @(
    "gateway", "web", "identity-api", "campaigns-api", "donations-worker",
    "elasticsearch", "prometheus", "grafana", "zabbix-server", "zabbix-web"
)
Write-Step "Aguardando o rollout dos Deployments..."
foreach ($d in $deployments) {
    kubectl rollout status "deployment/$d" -n $ns --timeout=300s
}

Write-Step "Estado final:"
kubectl get pods,svc -n $ns -o wide

Write-Host ""
Write-Host "Stack no ar (12 pods)." -ForegroundColor Green
Write-Host ""
Write-Host "Acesso local recomendado - port-forward (NAO passa pela NetworkPolicy):" -ForegroundColor Green
Write-Host "  kubectl port-forward -n $ns svc/web     18088:80   # App:  http://localhost:18088" -ForegroundColor Green
Write-Host "  kubectl port-forward -n $ns svc/gateway 18080:80   # API:  http://localhost:18080/api/..." -ForegroundColor Green
Write-Host ""
Write-Host "NodePort (30088/30080) so funciona com o nginx ingress controller: a NetworkPolicy" -ForegroundColor DarkGray
Write-Host "default-deny-ingress bloqueia acesso direto, liberando apenas o ingress." -ForegroundColor DarkGray
Write-Host "Com nginx ingress + hosts (conexao-solidaria.local -> 127.0.0.1): http://conexao-solidaria.local/" -ForegroundColor DarkGray
