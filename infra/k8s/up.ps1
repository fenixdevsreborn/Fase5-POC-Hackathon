# =============================================================================
# up.ps1 - Sobe TODO o stack do Conexao Solidaria no Kubernetes do Docker Desktop
# com UM comando, incluindo o Secret (gerado a partir do .env da raiz do repo).
#
# As imagens das 5 apps vem do Docker Hub (junonn5/conexao-solidaria-<svc>:latest,
# publicadas por infra/k8s/push-dockerhub.ps1); o node baixa direto do registry, sem
# o antigo ctr import. O Keel fica no cluster observando essas tags e recria os pods
# quando um novo push de :latest muda o digest.
#
# Faz, em ordem (fluxo validado ao vivo - ver ReadmeKubernetes.md):
#   1. seleciona o contexto docker-desktop
#   2. (opcional, -Publish) build + push das 5 imagens para o Docker Hub
#   3. cria o namespace, o PVC das Data Protection keys e o Secret
#      'conexao-solidaria-secret' a partir do .env
#   4. instala o Keel (auto-update das imagens)
#   5. kubectl apply -k (postgres, rabbitmq, es, Jobs de migracao, deployments)
#   6. espera StatefulSets + Jobs de migracao e reinicia as APIs (evita CrashLoop)
#   7. aguarda o rollout e imprime pods/services + URLs de acesso
#   8. sobe os port-forwards em segundo plano (ja liberados; use -NoForward p/ desligar)
#   9. imprime as credenciais de acesso (Blazor/gestor, RabbitMQ, Grafana, Zabbix)
#      com os valores reais do .env - ver "Acessos e credenciais padrao" no README
#
# Uso:
#   pwsh infra/k8s/up.ps1                # deploy puxando as imagens do Docker Hub
#   $env:DOCKERHUB_TOKEN="<PAT>"
#   pwsh infra/k8s/up.ps1 -Publish       # publica no Docker Hub antes de subir
#   pwsh infra/k8s/up.ps1 -NoForward     # nao inicia os port-forwards automaticos
#
# Pre-requisitos:
#   - Docker Desktop com Kubernetes habilitado (node 'desktop-control-plane').
#   - kubectl no PATH.
#   - .env preenchido na raiz do repo (ver .env.example).
#   - Imagens ja publicadas no Docker Hub (rode com -Publish, ou push-dockerhub.ps1 antes).
#   - Docker Compose parado (docker compose down) para nao conflitar portas.
# =============================================================================
[CmdletBinding()]
param(
    # Build + push das 5 imagens para o Docker Hub antes de subir a stack.
    [switch]$Publish,
    # Nao inicia os port-forwards automaticos ao final (apenas imprime os comandos).
    [switch]$NoForward,
    # Compat: aceito e ignorado (o build local + ctr import saiu do fluxo padrao).
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path   # ...\infra\k8s
$repoRoot  = Split-Path -Parent (Split-Path -Parent $scriptDir) # raiz do repo
$overlay   = Join-Path $scriptDir "overlays/local"
$ns        = "conexao-solidaria"
$node      = "desktop-control-plane"

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

# --- 2) (Opcional) Publicar as imagens no Docker Hub ------------------------
# As imagens das apps vem do Docker Hub; o node as baixa do registry (sem ctr
# import). Com -Publish, build + push das 5 imagens antes de subir a stack.
if ($SkipBuild) {
    Write-Host "    (-SkipBuild aceito e ignorado: o build local saiu do fluxo; use -Publish para publicar)" -ForegroundColor DarkGray
}
if ($Publish) {
    Write-Step "Publicando as imagens no Docker Hub (push-dockerhub.ps1)..."
    & (Join-Path $scriptDir "push-dockerhub.ps1")
    if ($LASTEXITCODE -ne 0) { throw "push-dockerhub.ps1 falhou" }
}
else {
    Write-Step "Usando as imagens ja publicadas no Docker Hub (junonn5/conexao-solidaria-*:latest)."
}

# --- 3) Namespace + Secret (gerado do .env) ---------------------------------
Write-Step "Aplicando o namespace..."
kubectl apply -f (Join-Path $scriptDir "base/namespace.yaml") | Out-Null

# PVC do key ring do Data Protection: aplicado A PARTE, fora do overlay, para que o
# `kubectl delete -k` do down.ps1 nao o remova a cada ciclo (sem isso, todo down/up
# gerava chaves novas e invalidava os JWTs ja gravados no localStorage dos browsers).
# Idempotente: se ja existe, o apply nao altera nada.
Write-Step "Aplicando o PVC das Data Protection keys (preservado entre ciclos)..."
kubectl apply -f (Join-Path $scriptDir "base/web-dataprotection-pvc.yaml") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "falha ao aplicar o PVC web-dataprotection-keys" }

# PVC das imagens de campanha: mesma logica: fora do overlay para que down/up nao apague
# as fotos ja enviadas (as campanhas no Postgres continuariam apontando para elas).
Write-Step "Aplicando o PVC das imagens de campanha (preservado entre ciclos)..."
kubectl apply -f (Join-Path $scriptDir "base/campaigns-images-pvc.yaml") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "falha ao aplicar o PVC campaigns-images" }

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
# openai-api-key e OPCIONAL (features de IA do Web): vazia = app sobe com as features ocultas.
$openAiKey = if ($envVars.ContainsKey("OPENAI_API_KEY")) { $envVars["OPENAI_API_KEY"] } else { "" }
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
    "--from-literal=openai-api-key=$openAiKey"
)
# Idempotente: dry-run gera o YAML e o apply cria/atualiza.
kubectl create secret generic conexao-solidaria-secret -n $ns @literals --dry-run=client -o yaml | kubectl apply -f -
if ($LASTEXITCODE -ne 0) { throw "falha ao aplicar o Secret" }

# --- 4) Keel (auto-update das imagens a partir do Docker Hub) ----------------
# Idempotente. Observa as tags :latest dos Deployments anotados (keel.sh/*) e
# recria os pods quando um novo push muda o digest. Ver infra/k8s/keel/keel.yaml.
Write-Step "Instalando/atualizando o Keel (auto-update)..."
kubectl apply -f (Join-Path $scriptDir "keel/keel.yaml")
if ($LASTEXITCODE -ne 0) { throw "falha ao aplicar o Keel" }

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

# --- 8) Port-forwards (ja liberados quando a stack sobe) --------------------
# Os Services sao ClusterIP e a NetworkPolicy default-deny bloqueia acesso direto;
# o caminho de acesso local e o port-forward. Aqui eles sobem sozinhos, em segundo
# plano, e continuam vivos apos o script terminar (svc/gateway ja pronto p/ Postman).
# Encerre com down.ps1, com -NoForward para nem subir, ou Stop-Process nos PIDs salvos.
$forwards = @(
    @{ Svc = "web";           Map = "18088:80";    Url = "http://localhost:18088";         Desc = "App (Blazor)" }
    @{ Svc = "gateway";       Map = "18080:80";    Url = "http://localhost:18080/api/...";  Desc = "API via Gateway (Postman)" }
    @{ Svc = "identity-api";  Map = "18081:80";    Url = "http://localhost:18081/swagger";  Desc = "Swagger Identity" }
    @{ Svc = "campaigns-api"; Map = "18082:80";    Url = "http://localhost:18082/swagger";  Desc = "Swagger Campaigns" }
    @{ Svc = "grafana";       Map = "3000:3000";   Url = "http://localhost:3000";           Desc = "Grafana" }
    @{ Svc = "prometheus";    Map = "9090:9090";   Url = "http://localhost:9090";           Desc = "Prometheus" }
    @{ Svc = "rabbitmq";      Map = "15672:15672"; Url = "http://localhost:15672";          Desc = "RabbitMQ Management" }
    @{ Svc = "zabbix-web";    Map = "8085:8080";   Url = "http://localhost:8085";           Desc = "Zabbix" }
)
$pidFile = Join-Path ([System.IO.Path]::GetTempPath()) "conexao-solidaria-portforward.pids"

Write-Host ""
if ($NoForward) {
    Write-Host "Port-forward automatico desativado (-NoForward). Comandos manuais:" -ForegroundColor Green
    foreach ($f in $forwards) {
        Write-Host ("  kubectl port-forward -n {0} svc/{1,-14} {2,-11} # {3}" -f $ns, $f.Svc, $f.Map, $f.Url) -ForegroundColor Green
    }
}
else {
    Write-Step "Liberando os port-forwards em segundo plano..."
    # Encerra port-forwards anteriores deste namespace (evita 'address already in use' no re-run).
    Get-CimInstance Win32_Process -Filter "Name = 'kubectl.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'port-forward' -and $_.CommandLine -match [regex]::Escape($ns) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    $logDir = Join-Path ([System.IO.Path]::GetTempPath()) "conexao-solidaria-pf-logs"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $pfPids = @()
    foreach ($f in $forwards) {
        $pfArgs = @("port-forward", "-n", $ns, "svc/$($f.Svc)", $f.Map)
        $p = Start-Process -FilePath "kubectl" -ArgumentList $pfArgs -WindowStyle Hidden -PassThru `
                 -RedirectStandardOutput (Join-Path $logDir "$($f.Svc).log") `
                 -RedirectStandardError  (Join-Path $logDir "$($f.Svc).err")
        $pfPids += $p.Id
        Write-Host ("    {0,-28} {1,-32} (svc/{2} {3})" -f $f.Desc, $f.Url, $f.Svc, $f.Map) -ForegroundColor Green
    }
    $pfPids | Set-Content -Path $pidFile
    Write-Host ""
    Write-Host "Port-forwards ativos e continuam apos este script (PIDs: $pidFile; logs: $logDir)." -ForegroundColor DarkGray
    Write-Host "Encerrar: pwsh infra/k8s/down.ps1  (ou Stop-Process -Id (Get-Content '$pidFile'))." -ForegroundColor DarkGray
}
# --- 9) Credenciais de acesso (lidas do .env ja carregado) ------------------
# Impressas em claro de proposito: e um ensaio LOCAL e a stack nao serve para nada
# se quem sobe nao souber com que usuario entrar. Os mesmos valores estao na secao
# "Acessos e credenciais padrao" do README.md.
Write-Host ""
Write-Host "==================== CREDENCIAIS DE ACESSO (ambiente local) ====================" -ForegroundColor Yellow
Write-Host ""

function Write-Cred($service, $url, $user, $password, $note) {
    Write-Host ("  {0}" -f $service) -ForegroundColor Yellow
    Write-Host ("     URL      {0}" -f $url) -ForegroundColor Gray
    Write-Host ("     usuario  {0}" -f $user) -ForegroundColor White
    Write-Host ("     senha    {0}" -f $password) -ForegroundColor White
    if ($note) { Write-Host ("     {0}" -f $note) -ForegroundColor DarkGray }
    Write-Host ""
}

# O gestor so existe se SEED_MANAGER_PASSWORD estava preenchida no primeiro start:
# sem ela o IdentityDatabaseInitializer pula o seed e nao ha usuario para o /gestor.
$seedPwd = if ($envVars.ContainsKey("SEED_MANAGER_PASSWORD") -and $envVars["SEED_MANAGER_PASSWORD"]) {
    $envVars["SEED_MANAGER_PASSWORD"]
} else {
    "(SEED_MANAGER_PASSWORD vazia no .env -> gestor NAO foi semeado)"
}

Write-Cred "App Blazor (gestor ONG)" "http://localhost:18088/entrar" `
    "gestor@conexaosolidaria.local" $seedPwd `
    "Doador: crie a conta em http://localhost:18088/cadastrar (a senha e voce quem define)."

Write-Cred "RabbitMQ Management" "http://localhost:15672" `
    (Require-Env 'RABBITMQ_USER') (Require-Env 'RABBITMQ_PASSWORD') `
    "Filas doacoes-recebidas, doacoes.retry.10s/60s, doacoes.dead-letter; exchanges conexao-solidaria (direct/outbox) e conexao-solidaria.notifications (fanout/SignalR)."

Write-Cred "Grafana" "http://localhost:3000" `
    (Require-Env 'GRAFANA_ADMIN_USER') (Require-Env 'GRAFANA_ADMIN_PASSWORD') `
    "Dashboards 'Conexao Solidaria' (Aplicacao, Negocio, Mensageria, Saude) ja provisionados."

# Atencao: ZABBIX_USER/ZABBIX_PASSWORD do .env sao as credenciais do POSTGRES do
# Zabbix, nao o login da UI. O frontend usa o default de fabrica Admin/zabbix.
Write-Cred "Zabbix" "http://localhost:8085" "Admin" "zabbix" `
    "Default de fabrica do frontend (ZABBIX_USER/ZABBIX_PASSWORD do .env sao do banco, nao da UI)."

Write-Host "  Prometheus http://localhost:9090 e os Swaggers (18081/18082) sobem sem autenticacao." -ForegroundColor DarkGray
Write-Host "  Valores vindos do .env em $envPath - use apenas localmente." -ForegroundColor DarkGray
Write-Host "===============================================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "Teste rapido no Postman (via gateway, ja liberado em http://localhost:18080):" -ForegroundColor Green
Write-Host "  POST http://localhost:18080/api/auth/cadastro-doador  (publico) - cria um doador e retorna o JWT" -ForegroundColor Green
Write-Host '        body (JSON): { "nomeCompleto": "Teste", "email": "teste@ex.com", "cpf": "<CPF valido>", "senha": "Senha@123" }' -ForegroundColor DarkGray
Write-Host "  POST http://localhost:18080/api/auth/login            (publico) - autentica e retorna o JWT" -ForegroundColor Green
Write-Host '        body (JSON): { "email": "teste@ex.com", "senha": "Senha@123" }' -ForegroundColor DarkGray
Write-Host "        gestor semeado: gestor@conexaosolidaria.local / $seedPwd" -ForegroundColor DarkGray
Write-Host "  GET  http://localhost:18080/api/auth/me               (Bearer)  - dados do usuario logado" -ForegroundColor Green
Write-Host "  GET  http://localhost:18080/api/campanhas/search      (publico) - lista/busca campanhas" -ForegroundColor Green
Write-Host "  Header nas rotas protegidas: Authorization: Bearer {accessToken retornado no login}" -ForegroundColor DarkGray
Write-Host ""
Write-Host "NodePort (30088/30080) so funciona com o nginx ingress controller: a NetworkPolicy" -ForegroundColor DarkGray
Write-Host "default-deny-ingress bloqueia acesso direto, liberando apenas o ingress." -ForegroundColor DarkGray
Write-Host "Com nginx ingress + hosts (conexao-solidaria.local -> 127.0.0.1): http://conexao-solidaria.local/" -ForegroundColor DarkGray
