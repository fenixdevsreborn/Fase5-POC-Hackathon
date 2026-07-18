---
name: verify
description: Sobe a stack do Conexao Solidaria localmente (sem k8s/compose) e dirige os fluxos pela API e pela UI Blazor, para observar uma mudanca rodando de verdade.
---

# Verificar o Conexao Solidaria localmente

O `docker-compose` nao sobe `gateway` nem `web`, e o k8s (`infra/k8s/up.ps1`) leva minutos.
Para verificar uma mudanca, sobe-se so o necessario com `dotnet run`, apontando o service
discovery do Aspire para portas locais.

## 1. Infra minima (so Postgres)

```bash
docker run -d --name cs-verify-pg -e POSTGRES_PASSWORD=verify \
  -e POSTGRES_DB=campaignsdb -p 55432:5432 postgres:16-alpine
docker exec cs-verify-pg psql -U postgres -c "CREATE DATABASE identitydb;"
```

RabbitMQ e Elasticsearch podem ficar fora: os dois degradam graciosamente. **Mas veja o
aviso de latencia do Elasticsearch mais abaixo** antes de medir tempo de resposta.

## 2. Servicos

Sempre `--no-build --no-launch-profile`. Sem `--no-launch-profile`, o `launchSettings.json`
sobrescreve o `ASPNETCORE_URLS` e o servico sobe em outra porta.

```bash
SECRET="verify-secret-super-longa-para-hmacsha256-conexao-solidaria-2026"

# Campaigns.Api (5066)
ASPNETCORE_URLS="http://127.0.0.1:5066" \
ConnectionStrings__CampaignsDb="Host=127.0.0.1;Port=55432;Database=campaignsdb;Username=postgres;Password=verify" \
Jwt__Issuer=ConexaoSolidaria Jwt__Audience=ConexaoSolidaria Jwt__Secret="$SECRET" \
CampaignImages__RootPath="/tmp/uploads" Migrations__RunOnStartup=true \
dotnet run --project src/ConexaoSolidaria.Campaigns.Api --no-build --no-launch-profile

# Identity.Api (5201) — Seed__Gestor__Senha cria o gestor; sem ela o seed e pulado
ASPNETCORE_URLS="http://127.0.0.1:5201" \
ConnectionStrings__IdentityDb="Host=127.0.0.1;Port=55432;Database=identitydb;Username=postgres;Password=verify" \
Jwt__Issuer=ConexaoSolidaria Jwt__Audience=ConexaoSolidaria Jwt__Secret="$SECRET" \
Seed__Gestor__Senha="Gestor@Local2026" \
dotnet run --project src/ConexaoSolidaria.Identity.Api --no-build --no-launch-profile

# Gateway (5100) e Web (5288) — precisam de `env` (ver gotcha dos hifens)
env ASPNETCORE_URLS="http://127.0.0.1:5100" \
  "services__identity-api__http__0=http://127.0.0.1:5201" \
  "services__campaigns-api__http__0=http://127.0.0.1:5066" \
  dotnet run --project src/ConexaoSolidaria.Gateway --no-build --no-launch-profile

env ASPNETCORE_URLS="http://127.0.0.1:5288" ASPNETCORE_ENVIRONMENT=Development \
  "services__gateway__http__0=http://127.0.0.1:5100" \
  dotnet run --project src/ConexaoSolidaria.Web --no-build --no-launch-profile
```

Gestor: `gestor@conexaosolidaria.local` / `Gestor@Local2026`.

## 3. Dirigir a UI (Playwright)

```bash
npm install playwright   # no scratchpad
```

O Chromium do pacote pode nao bater com o ja instalado. Em vez de baixar outro, aponte para
o existente (`ls ~/AppData/Local/ms-playwright`):

```js
chromium.launch({ executablePath: 'C:/Users/<voce>/AppData/Local/ms-playwright/chromium-1217/chrome-win64/chrome.exe' })
```

Seletores da tela de login (inputs HTML puros, nao MudBlazor):
`input#identificador` (e-mail ou CPF) e `input#senha`. `getByLabel(/senha/i)` da strict-mode
violation — casa tambem o botao "Mostrar senha".

Na tela de lote ha **varios** `input[type=file]` (imagem da campanha e importacao de planilha),
e a ordem muda conforme o formulario re-renderiza. Sempre escope:
`.nova-campanha__ai--import input[type=file]` para a planilha.

## Gotchas

- **Variaveis com hifen** (`services__campaigns-api__http__0`) nao podem ser atribuidas inline
  no bash (`VAR=x cmd` vira "No such file or directory"). Use o comando `env`.
- **Rebuild com servico rodando falha** (MSB3027/MSB3021: o .exe fica bloqueado). Pare o
  processo antes de recompilar.
- **Elasticsearch fora do ar custa ~12s por chamada de indexacao** (timeout de conexao). Se for
  medir latencia de escrita, ou suba o ES, ou lembre que esse tempo e o timeout — foi assim que
  se descobriu que a criacao em lote indexava item a item.
- **Blazor Server**: fechar o browser cancela a requisicao em voo no circuito. Espere a operacao
  terminar antes do `browser.close()`, senao ela e abortada no meio.

## Limpeza

```bash
docker rm -f cs-verify-pg
```
