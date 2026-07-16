# =============================================================================
# down.ps1 - Derruba a stack do Conexao Solidaria no Kubernetes local.
#
# Uso:
#   pwsh infra/k8s/down.ps1              # remove os recursos do overlay local
#   pwsh infra/k8s/down.ps1 -PurgeData   # tambem apaga PVCs e o namespace (perde dados)
#
# Por padrao, os PVCs de Postgres/RabbitMQ (volumeClaimTemplates) NAO sao
# removidos - proposital, para nao perder dados por engano.
# =============================================================================
[CmdletBinding()]
param(
    [switch]$PurgeData
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$overlay   = Join-Path $scriptDir "overlays/local"
$ns        = "conexao-solidaria"

Write-Host "==> Removendo a stack (kubectl delete -k)..." -ForegroundColor Cyan
kubectl delete -k $overlay --ignore-not-found

if ($PurgeData) {
    Write-Host "==> Apagando PVCs e o namespace (perde dados)..." -ForegroundColor Yellow
    kubectl delete pvc -n $ns --all --ignore-not-found
    kubectl delete namespace $ns --ignore-not-found
}
else {
    Write-Host "PVCs de Postgres/RabbitMQ preservados. Para apagar tudo:" -ForegroundColor DarkGray
    Write-Host "  pwsh infra/k8s/down.ps1 -PurgeData" -ForegroundColor DarkGray
}
