# Relatório Técnico — Conexão Solidária

> Documento-síntese da entrega. Reúne, num único lugar, **o que foi construído, por que, e como se verifica**.
> Público-alvo: avaliadores do desafio e novos integrantes do time.
> Fonte da verdade cruzada: código-fonte do repositório, `docs/arquitetura.md`,
> `docs/decisoes-arquiteturais.md` (`AD-01..AD-35`), `docs/funcionalidades.md`,
> `docs/api-reference.md`, `docs/runbook.md` e os READMEs de Kubernetes/Observabilidade.

---

## Sumário

1. [Sumário executivo](#1-sumário-executivo)
2. [Arquitetura](#2-arquitetura)
3. [O que foi entregue por fase](#3-o-que-foi-entregue-por-fase)
4. [Melhorias de performance e arquitetura](#4-melhorias-de-performance-e-arquitetura)
5. [Funcionalidades de usuário adicionadas](#5-funcionalidades-de-usuário-adicionadas)
6. [Padrões aplicados](#6-padrões-aplicados)
7. [Qualidade e testes](#7-qualidade-e-testes)
8. [Deploy e operação](#8-deploy-e-operação)
9. [Segurança](#9-segurança)
10. [Matriz de rastreabilidade](#10-matriz-de-rastreabilidade)
11. [Limitações e dívidas técnicas conscientes](#11-limitações-e-dívidas-técnicas-conscientes)
12. [Próximos passos](#12-próximos-passos)

---

## 1. Sumário executivo

**Conexão Solidária** é uma plataforma social para o desafio da ONG Esperança Solidária:
uma vitrine pública de campanhas, uma área de doador e um painel de gestor, sustentados por
um backend de microsserviços .NET 10 orquestrado com **.NET Aspire**.

A **tese de arquitetura** tem três pilares:

- **Doações assíncronas** — a intenção de doação é aceita imediatamente (`202 Accepted`), mas o
  valor arrecadado só muda depois que um **Worker** consome o evento correspondente. A API **não**
  atualiza o total diretamente; recebimento e processamento são desacoplados por mensageria.
- **Rastreabilidade ponta a ponta** — nenhuma doação aceita pode se perder (Outbox transacional +
  publisher confirms) e nenhuma pode ser contabilizada em dobro (idempotência de entrada por
  `Idempotency-Key` e de consumo por `EventId`). O caminho da doação é observável por `traceparent`
  propagado do HTTP até o Worker e por `X-Correlation-Id` injetado no Gateway.
- **Transparência** — painéis públicos (sem login) expõem o total arrecadado por campanha ativa,
  com atualização em tempo real na UI do doador quando a doação é processada.

### Estado final em números

| Indicador | Valor |
| --- | --- |
| Projetos de aplicação/infra | **9** (`src/`) |
| Projetos de teste | **2** (`tests/`) |
| Testes automatizados | **35 verdes** — 23 unitários + 12 de integração (Testcontainers) |
| Build | **0 erros** (Release) |
| Imagens Docker | **5** (Identity, Campaigns, Worker, Gateway, Web) — todas buildam |
| Deploy Kubernetes | **Validado ao vivo** — 12 pods `Running 1/1`, fluxo E2E de doação processado |
| Decisões arquiteturais registradas | **AD-01..AD-35** |
| Requisitos do desafio | **100% dos obrigatórios + os 2 bônus** cobertos |

Todas as fases do plano foram concluídas (Fase 0 a 6), seguidas de 14 melhorias de
performance/arquitetura e 5 novas funcionalidades de usuário.

---

## 2. Arquitetura

### 2.1 Componentes e responsabilidades

A solução é um **monólito distribuído pequeno**: serviços independentes no deploy, mas com
padrões de infra unificados (`ServiceDefaults`) e um domínio compartilhado onde faz sentido.
O diagrama de componentes está em [`docs/arquitetura.md`](arquitetura.md).

| Componente | Responsabilidade |
| --- | --- |
| **AppHost** (`.NET Aspire`) | Orquestra o ambiente local completo (bancos, fila, busca, serviços) com **um comando**; expõe o Aspire Dashboard (logs, traces, métricas, health). |
| **ServiceDefaults** | OpenTelemetry (traces/métricas via OTLP), health checks `/health` + `/alive`, service discovery e resiliência HTTP (`AddStandardResilienceHandler`) — referenciado por todos os serviços. |
| **Contracts** | Tipos puros **sem EF**: eventos (`DoacaoRecebidaEvent`, `DoacaoProcessadaNotification`), roles/`JwtOptions`, Value Object `Cpf` + validação, helpers de RabbitMQ. Assembly neutro de contrato compartilhado. |
| **Shared** | Domínio EF (`Campaign`, `Donation`, `OutboxMessage`, `ProcessedMessage`, `DonationIdempotencyKey`, `CampaignStats`, `DomainRuleException`) e o **`CampaignsDbContext` único** com migrations. |
| **Gateway** (YARP) | Ponto único de entrada de `/api/*`. Roteia por service discovery; injeta `X-Correlation-Id`; aplica **rate limiting** (auth 10/min, doação 30/min, global 100/min por IP); **security headers** + HSTS; expõe `/metrics`. |
| **Web** (Blazor Server + MudBlazor) | UI pública, área do doador e painel do gestor. Fala só com o Gateway. Auth JWT em `ProtectedLocalStorage`; Data Protection keys persistidas. Consome o fanout de notificações e empurra atualizações em tempo real. |
| **Identity.Api** | Cadastro de doadores, login e emissão de JWT (roles `GestorONG`/`Doador`); policies nomeadas; ProblemDetails; API versioning. Referencia **só `Contracts`**. |
| **Campaigns.Api** | CRUD + ciclo de vida de campanhas; busca **fuzzy multi-campo** no ES (índice com analisador pt-BR criado no startup + backfill do Postgres) com fallback ES→Postgres (circuit breaker); transparência (output-cached); intenção de doação (`202` + Outbox + Idempotency-Key); status enriquecido; "minhas doações"; `/stats` (read model). Dona do `campaignsdb`. |
| **Donations.Worker** | Consumidor **idempotente** (dedup por `EventId`), **retry 10s/60s + DLQ**, **incremento atômico**, **upsert do read model** na mesma transação, publica a notificação de conclusão. |

### 2.2 Fluxo assíncrono completo

1. O doador registra a intenção pela Web → `POST /api/doacoes` (via Gateway) com um `Idempotency-Key`.
2. A **Campaigns.Api** grava, **na mesma transação**, o `Donation` (status `Pendente`) e uma
   `OutboxMessage` com o `DoacaoRecebidaEvent`. Se o `Idempotency-Key` já existir, retorna a doação
   original. Responde **`202 Accepted`**.
3. Um **dispatcher** varre `outbox_messages` pendentes e publica no RabbitMQ (exchange
   `conexao-solidaria`, fila `doacoes-recebidas`) com **publisher confirms** e propagação de
   `traceparent`. Falha de publicação mantém a mensagem pendente para nova tentativa.
4. O **Donations.Worker** consome o evento. Se o `EventId` já está em `processed_messages`, descarta
   (idempotência). Caso contrário, numa transação atômica: incrementa `campaigns.ValorTotalArrecadado`
   (`ExecuteUpdateAsync`), faz **upsert** de `campaign_stats`, marca a `Donation` como `Processada` e
   registra o `EventId`.
5. Erros de consumo seguem para `doacoes.retry.10s` → `doacoes.retry.60s` e, esgotadas as tentativas,
   para `doacoes.dead-letter` (DLQ).
6. A confirmação chega à UI por **dois caminhos complementares** — tempo real (ver 2.3) e **polling**
   de fallback em `GET /api/doacoes/{id}`, que só declara "Concluída" quando o status vira `Processada`.
   A UI nunca confia apenas no `202`.

O evento é **versionado** (`SchemaVersion` + `CorrelationId`) para evolução de contrato sem quebra.

### 2.3 Fluxo de notificação em tempo real

Ao processar a doação com sucesso — e **só no sucesso**, de forma **best-effort** — o Worker publica
uma `DoacaoProcessadaNotification` num **exchange fanout dedicado** `conexao-solidaria.notifications`
(separado do fluxo transacional). O `ConexaoSolidaria.Web` roda um `NotificationConsumer`
(BackgroundService resiliente) que declara o fanout, cria uma **fila anônima exclusiva/auto-delete**
(uma por réplica) e reemite via `NotificationDispatcher` para as telas Blazor sobre o **circuito
SignalR** do Blazor Server. O consumer nunca lança para fora do `ExecuteAsync`; em falha de broker,
**reconecta com backoff exponencial (5s→60s)** e o polling permanece como fallback (`AD-30`).

---

## 3. O que foi entregue por fase

Para cada fase: **o problema**, **o que foi feito** e a **evidência/verificação**.

### Fase 0 — Segurança (base)

- **Problema:** segredos (JWTs/senhas) não podem viver no repositório; dados sensíveis (CPF) precisam
  de tratamento consistente.
- **Feito:** remoção de todos os segredos versionados; `.env.example`, `infra/k8s/secret.example.yaml`
  e `SECURITY.md`; senha do gestor seed via env `Seed__Gestor__Senha`; CPF como **Value Object** com
  máscara; ProblemDetails, policies, rate limiting e security headers.
- **Evidência:** ausência de segredos no repo; `SECURITY.md`; `AD-15`, `AD-17`, `AD-18`.

### Fase 1 — Orquestração com Aspire

- **Problema:** 5 processos + Postgres + RabbitMQ + Elasticsearch são caros de subir à mão em dev.
- **Feito:** `ConexaoSolidaria.AppHost` sobe o grafo inteiro com `dotnet run`, injeta connection
  strings/descoberta de serviço e publica telemetria no Aspire Dashboard; `ServiceDefaults`
  compartilhado padroniza OTel, health e resiliência HTTP.
- **Evidência:** `dotnet run --project src/ConexaoSolidaria.AppHost`; `AD-01`, `AD-02`.

### Fase 2 — Frontend Blazor

- **Problema:** front administrativo/vitrine em C#, sem introduzir cadeia Node/npm no build.
- **Feito:** **Blazor Web App (Interactive Server)** + **MudBlazor**; auth JWT em
  `ProtectedLocalStorage` + `AuthenticationStateProvider` custom; ApiClient tipado (resiliência
  herdada); Data Protection keys persistidas (preparação multi-réplica).
- **Evidência:** rotas em `ConexaoSolidaria.Web` (`/`, `/campanhas`, `/transparencia`, `/doador`,
  `/gestor`, ...); `AD-03`.

### Fase 3 — Mensageria resiliente

- **Problema:** registrar a doação no Postgres **e** publicar no RabbitMQ é *dual-write* (um pode
  falhar após o outro); com entrega at-least-once, a mesma mensagem pode chegar mais de uma vez;
  falhas não podem descartar doações nem travar a fila; o total pode sofrer *lost update* sob
  concorrência.
- **Feito:**
  - **Outbox transacional** (doação + evento na mesma transação) + dispatcher com **publisher confirms**.
  - **Idempotência de consumo** por `EventId` em `processed_messages`.
  - **Retry escalonado** (`doacoes.retry.10s` → `doacoes.retry.60s`) + **DLQ** (`doacoes.dead-letter`).
  - **Evento versionado** (`SchemaVersion` + `CorrelationId`).
  - **Incremento atômico** do arrecadado via `ExecuteUpdateAsync` (evita lost update).
- **Evidência:** 12 testes de integração cobrem outbox, fluxo assíncrono, idempotência por `EventId`
  e por `Idempotency-Key`, e `campaign_stats`; `AD-05..AD-09`.

### Fase 4 — Observabilidade

- **Problema:** era preciso enxergar negócio, aplicação e mensageria em tempo quase real, com
  correlação do fluxo assíncrono.
- **Feito:** OTel via `ServiceDefaults` (OTLP); `/metrics` (prometheus-net) em todos os serviços
  (incl. Gateway); **OTel Collector** → **Tempo** (traces) + **Prometheus** (métricas); **3 dashboards
  Grafana** (`negocio`, `aplicacao`, `mensageria`) + alertas (DLQ>0, Outbox>20 por 2min, 5xx);
  **template Zabbix real** com itens HTTP + 9 triggers. Métricas custom `conexao_*`
  (`_donations_processed_total`, `_rejected_total`, `_donation_publish_total`,
  `_publish_failures_total`, `_outbox_pending_messages`, `_donation_processing_duration_seconds`,
  `_dead_letter_messages`).
- **Evidência:** `infra/grafana/`, `infra/otel/`, `infra/zabbix/`; `docs/runbook.md`,
  `docs/cenario-falha-recuperacao.md`; `AD-20`, `AD-21`, `AD-29`.

### Fase 5 — Kubernetes (Kustomize)

- **Problema:** sair de um manifesto monolítico com `emptyDir`/NodePorts abertos para algo próximo de
  produção.
- **Feito:** **Kustomize** (base + overlay `local`): StatefulSet+PVC (postgres, rabbitmq), PVC
  (elasticsearch), tudo **ClusterIP** com **Ingress nginx** de entrada única; probes
  (startup `/alive`, readiness `/health`, liveness `/alive`); requests/limits; **securityContext**
  (`runAsNonRoot`, `runAsUser 10001`, `readOnlyRootFilesystem`, `drop ALL`, seccomp RuntimeDefault);
  **11 NetworkPolicies** (default-deny + allow-list, incl. `web→rabbitmq` e `migrations→postgres`);
  **HPA** (gateway/identity/campaigns por CPU); **PDB**; **Jobs de migração** dedicados
  (`RunMigrationsOnly=true`; deployments com `Migrations__RunOnStartup=false`); `smoke.ps1`.
- **Evidência:** deploy validado ao vivo (seção 8); `kubectl kustomize infra/k8s/overlays/local`
  renderiza; `AD-19`, `AD-28`; `ReadmeKubernetes.md`, `infra/k8s/README.md`.

### Fase 6 — CI/CD

- **Problema:** garantir build, testes e imagens reproduzíveis a cada push, com validação de manifestos
  e publicação.
- **Feito:** workflow multi-job em `.github/workflows/ci.yml`:
  - **quality** — restore + cache NuGet, format/scan de vulneráveis (report-only), build Release.
  - **tests** — unit + integração (Testcontainers), coverage XPlat, artifact de resultados.
  - **containers** — matrix das **5 imagens** (Buildx + cache), sem push.
  - **kubernetes-validation** — `kustomize build` + `kubeconform`.
  - **publish** — só em `main`: push das 5 imagens para o **GHCR** (tags `:sha` e `:latest`).
  - **Registry do ambiente local:** o cluster consome o **Docker Hub**
    (`junonn5/conexao-solidaria-*:latest`, via `infra/k8s/push-dockerhub.ps1`), observado pelo
    **Keel** para auto-update. GHCR = rastreabilidade por commit; Docker Hub = consumo local.
  - PR template Spec-Driven, badge de CI no README.
- **Evidência:** `.github/workflows/ci.yml` (jobs `quality`, `tests`, `containers`,
  `kubernetes-validation`, `publish`).

---

## 4. Melhorias de performance e arquitetura

Após as fases, foram executadas **14 melhorias** organizadas em duas frentes internas:
**A (performance/API)** e **B (arquitetura/refatoração de acoplamento)**. Cada uma está registrada como
decisão (`AD-25..AD-34`, além de itens já cobertos por ADRs anteriores). B3 (MassTransit) foi
**avaliada e adiada** — ver `AD-24`.

### Frente A — Performance / API

| # | O que mudou | Valor | Ref. |
| --- | --- | --- | --- |
| **A1** | Detalhe **público** de campanha (`GET /api/campanhas/{id}` anônimo), com projeção direta `AsNoTracking` para o DTO | Página `/campanhas/{id}` sem exigir login; sem materializar a entidade rica | `funcionalidades.md` V2 |
| A2 | **Output caching** (~5s) nos reads públicos anônimos (`search`, `transparencia`, `stats`) | Reduz pressão em Postgres/ES em picos; casa com a consistência eventual do total | `AD-31` |
| A3 | **Read model `campaign_stats`** consultado por `GET /api/campanhas/stats` | Dashboard sem agregação em runtime (O(campanhas)) | `AD-26` |
| A4 | **Índices de performance** (`AddPerformanceIndexes`): `campaigns(Status,DataFim)`, `donations(DoadorId)`, outbox parcial `WHERE PublishedAtUtc IS NULL` | Consultas de transparência, histórico e varredura do Outbox mais baratas | Migration `AddPerformanceIndexes` |
| A5 | **Incremento atômico** do arrecadado (`ExecuteUpdateAsync`) | Sem lost update sob concorrência, sem lock pessimista | `AD-09` |
| A6 | **Prefetch (QoS 10)** no consumidor de doações | Equilíbrio vazão × backpressure no Worker | `AD-33` |
| A7 | **Circuit breaker (Polly)** no fallback de busca ES→Postgres | Com ES fora, busca cai direto no Postgres **sem pagar timeout** repetido | `AD-34` |

### Frente B — Arquitetura / refatoração

| # | O que mudou | Valor | Ref. |
| --- | --- | --- | --- |
| **B1** | Projeto **`Contracts`** puro (sem EF); Identity referencia **só `Contracts`** | Identity para de arrastar EF/Npgsql/migrations que nunca usa; contrato num assembly neutro | `AD-25` |
| B2 | **API versioning** por header/query (`x-api-version`/`api-version`), default `1.0`, sem versionar rota | Evolução de contrato sem quebrar rotas atuais nem o roteamento do Gateway | `AD-27` |
| **B3** | **MassTransit** — **avaliado e adiado** (plano de migração faseado documentado) | Preserva um núcleo artesanal testado e estável; evita reescrever 12 testes e a topologia por ganho baixo no escopo atual | `AD-24` |
| B4 | **Notificações em tempo real** (fanout dedicado + consumer resiliente no Web) | Feedback em tempo real sobre um fallback confiável (polling) | `AD-30` |
| B5 | **EF `EnableRetryOnFailure`** + **execution strategy** explícita no Worker | Resiliência a falhas transitórias de banco; retry reexecuta a transação atômica inteira | `AD-32` |
| B6 | **Tracing distribuído** com OTel Collector + **Grafana Tempo** (fora do Aspire) | Trace ponta a ponta (incl. salto assíncrono pela fila) em Compose/k8s | `AD-29` |
| B7 | **Data Protection keys persistidas** + sticky cookie no Ingress (preparação multi-réplica do Web) | Caminho para escalar o Blazor Server horizontalmente | `AD-03` |
| **B8** | **Migração como Job dedicado** (um migrador por banco); deployments com `RunOnStartup=false` | Schema aplicado uma vez, sem corrida entre réplicas; readiness desacoplada do Migrate | `AD-28` |

### Frente C — Qualidade da busca (posterior às frentes A/B)

O ES estava subaproveitado: o índice nascia por **mapeamento dinâmico** (analisador `standard`),
então a busca era acento-sensível e restrita a título/descrição. Reconfigurado para entregar o que
o Elasticsearch tem de melhor:

| # | O que mudou | Valor | Ref. |
| --- | --- | --- | --- |
| **C1** | **Índice explícito com analisador pt-BR** criado no startup (`asciifolding`, stemmer `light_portuguese`, stopwords), no lugar do mapeamento dinâmico | Busca deixa de ser acento-sensível e passa a tratar plural/derivação: `sao paulo` acha "São Paulo"; `metrica` acha "Metricas" | `AD-35` |
| C2 | **Query fuzzy multi-campo** (`bool/should`: `best_fields`+`fuzziness AUTO`, `phrase_prefix`, `edge_ngram`, `match_phrase` com boost) sobre **título + descrição + categoria** | Corrige digitação (`cadera gamer` → "Cadeira Gamer"), autocompleta por prefixo (`comput`) e busca por categoria; frase exata rankeia no topo | `AD-35` |
| C3 | **Backfill automático** do Postgres quando o índice é criado | Fecha o gap "só campanhas criadas após a integração são indexadas" (`AD-12`); índice vira **reconstruível** a partir da fonte da verdade | `AD-35` |
| C4 | **Remoção da query `Count` duplicada** (`TrackTotalHits` + `response.Total`) | Metade das idas ao ES por busca | `AD-35` |

Validado contra um **Elasticsearch 8.15.3 real** (typo, acento, stemming, prefixo e categoria) e
no cluster k8s, com o índice recriado e backfill das campanhas existentes. Ressalva honesta: os
testes de integração usam `FakeCampaignSearchRepository`, então mapeamento e query **não têm
cobertura automatizada** — a verificação foi manual contra o ES real.

---

## 5. Funcionalidades de usuário adicionadas

Cinco funcionalidades novas de usuário, além do fluxo base:

1. **Minhas doações** — histórico do doador em `GET /api/doacoes/minhas`, página `/doador/doacoes`.
2. **Recibo / comprovante** — detalhe da doação com status enriquecido (título da campanha + datas)
   em `GET /api/doacoes/{id}`, página `/doador/doacoes/{id}`.
3. **Ações de ciclo de vida da campanha** — `POST /api/campanhas/{id}/ativar|concluir|cancelar`
   (Gestor), refletindo as transições de domínio `Ativar/Concluir/Cancelar`.
4. **Notificações em tempo real** — via fanout `conexao-solidaria.notifications` → `NotificationConsumer`
   → SignalR (ver 2.3).
5. **Valores rápidos na doação** — atalhos de valor no fluxo de doação a partir de `/campanhas/{id}`.

---

## 6. Padrões aplicados

| Padrão | Onde / como |
| --- | --- |
| **Outbox transacional** | Doação + evento gravados na mesma transação; dispatcher publica com publisher confirms (`AD-05`). |
| **Idempotent consumer / inbox** | Dedup por `EventId` em `processed_messages` no Worker (`AD-06`). |
| **Idempotência de entrada** | Header `Idempotency-Key` → `donation_idempotency_keys` na API (`AD-14`). |
| **Retry escalonado + DLQ** | `doacoes.retry.10s/60s` → `doacoes.dead-letter` (`AD-07`). |
| **CQRS read model** | `campaign_stats` escrito pelo Worker, lido por `/stats` (`AD-26`). |
| **Value Object** | `Cpf` (validação + máscara, imutável) em `Contracts` (`AD-15`). |
| **ProblemDetails (RFC 7807)** | 422 (validação/regra de domínio), 409 (conflito), 404, 401/403, 429 (`AD-13`). |
| **API Gateway / BFF** | YARP como entrada única de `/api/*` (`AD-04`). |
| **Policies / RBAC** | Roles `GestorONG`/`Doador`; policies `CampaignManagement`/`DonationCreation` (`AD-16`). |
| **Rate limiting** | No Gateway: auth 10/min, doação 30/min, global 100/min por IP; 429 + `Retry-After` (`AD-18`). |
| **Circuit breaker** | Polly no fallback de busca ES→Postgres (`AD-34`). |
| **EF Migrations** | Schema versionado por `MigrateAsync`; substitui `EnsureCreated` (`AD-11`). |
| **Service discovery** | Nomes lógicos resolvidos por DNS/Aspire; NetworkPolicy reforça o isolamento (`AD-04`). |
| **Distributed tracing** | `traceparent` propagado HTTP → RabbitMQ → Worker; `X-Correlation-Id` no Gateway (`AD-20`, `AD-29`). |
| **Incremento atômico** | `ExecuteUpdateAsync` no Worker (`AD-09`). |
| **Bounded context único, dois processos** | API + Worker compartilham `CampaignsDbContext` deliberadamente (`AD-23`). |

---

## 7. Qualidade e testes

**35 testes verdes** — 23 unitários + 12 de integração. Executáveis com `dotnet test ConexaoSolidaria.slnx`.

### 7.1 Testes unitários — `tests/ConexaoSolidaria.Tests` (23)

Focados em **regras de domínio** e **persistência EF com SQLite in-memory**, sem dependências externas.

| Suíte | Cobre |
| --- | --- |
| `CampaignRuleTests` | Regras de criação de campanha (meta > 0, `DataFim` futura). |
| `CampaignTransitionTests` | Transições de estado `Ativar/Concluir/Cancelar` e violações. |
| `DonationDomainTests` | Estados da doação (`Pendente/Processada/Rejeitada/Falha`) e transição única. |
| `CpfValidatorTests` | Validação de CPF (Value Object) com casos `[Theory]`/`[InlineData]`. |
| `OutboxTests` | Gravação e semântica do Outbox. |

### 7.2 Testes de integração — `tests/ConexaoSolidaria.IntegrationTests` (12)

Usam **Testcontainers** subindo **Postgres + RabbitMQ reais** (atributo `[DockerFact]`, pulados
quando o Docker não está disponível). Cobrem:

| Suíte | Cobre |
| --- | --- |
| `IdentityApiTests` | Cadastro de doador, login, emissão de JWT, erros ProblemDetails. |
| `CampaignsApiTests` | CRUD/consulta de campanhas, transparência, contrato de erro. |
| `AsyncFlowTests` | **Fluxo assíncrono completo** (doação → Outbox → broker → Worker → arrecadado), **idempotência por `EventId`**, **idempotência por `Idempotency-Key`** e **`campaign_stats`**. |

O fluxo crítico é testado contra infra real (não mocks), o que dá confiança sobre Outbox,
idempotência, retry/DLQ e incremento atômico ponta a ponta (`AD-22`). No CI, a cobertura é coletada
com XPlat Code Coverage e publicada como artifact.

---

## 8. Deploy e operação

Três caminhos, do mais simples ao mais próximo de produção:

### 8.1 Aspire (desenvolvimento — recomendado)

```powershell
dotnet run --project src/ConexaoSolidaria.AppHost
```

Sobe **tudo** (Postgres, RabbitMQ, Elasticsearch, as 3 APIs, o Worker, o Gateway e a Web) com **um
comando** e imprime a URL do Aspire Dashboard. Segredos de dev são gerados/defaultados; para uso real,
sobrescrever via user-secrets.

### 8.2 Docker Compose (alternativa)

`docker compose up --build` (após `Copy-Item .env.example .env` e preencher os segredos). Reproduz o
ambiente completo com Grafana/Prometheus/Zabbix em portas fixas. O schema é criado por **EF Migrations**
no start — ao vir de uma stack antiga criada com `EnsureCreated`, recriar os volumes (`down -v`).

### 8.3 Kubernetes (Kustomize) — **deploy validado ao vivo**

Fluxo: publicação das 5 imagens no **Docker Hub** (`push-dockerhub.ps1`) → Secret → **Keel** →
`kubectl apply -k infra/k8s/overlays/local` (`smoke.ps1` opcional). Tudo isso em um comando:
`pwsh infra/k8s/up.ps1`, que ainda deixa os port-forwards liberados ao final.

As imagens vivem em `junonn5/conexao-solidaria-<svc>:latest` (repositórios públicos) e os
Deployments usam `imagePullPolicy: Always` — o antigo passo de `docker save | ctr images import`
no node kind foi **eliminado**. O **Keel** (`infra/k8s/keel/keel.yaml`) observa essas tags via
poll (`keel.sh/pollSchedule: "@every 1m"`, política `force`) e recria os pods automaticamente
quando o digest de `:latest` muda — entrega contínua até o cluster local.

**Resultado do deploy validado** (Docker Desktop k8s v1.36.1):

- **12 pods `Running 1/1`**; Jobs de migração `Complete`.
- **E2E de doação processado em ~3s**; read model `campaign_stats` populado; consumer de notificações
  conectado.

**Achados do deploy (registrados honestamente):**

1. **NetworkPolicy dos Jobs de migração** — com default-deny, os Jobs (`app=*-migrations`) precisaram
   de regra explícita para acessar o Postgres. **Sem isso o schema não nasce** e os apps entram em
   CrashLoop esperando tabelas que nunca chegam. A allow-list `migrations→postgres` é obrigatória (`AD-28`).
2. **Timing do schema-wait** — o loop de espera de schema dos apps (~30s) é curto; se Postgres/Job de
   migração demorarem, os pods podem entrar em CrashLoop antes de o schema existir. Recomendação
   registrada: **initContainer** que aguarda a migração concluir (hoje mitigado pelo backoff de restart
   do pod).
3. **kubeconfig do docker-desktop** — CA desatualizada exigiu `insecure-skip-tls-verify` local.

Operação (alertas, DLQ, cenário de falha/recuperação): `docs/runbook.md` e
`docs/cenario-falha-recuperacao.md`.

---

## 9. Segurança

- **Sem segredos no repositório** — removidos JWTs/senhas versionados; `.env.example`,
  `infra/k8s/secret.example.yaml` e `SECURITY.md`. `secret.yaml` é gitignored.
- **Gestão de segredos por ambiente** — `.env` (Compose), **user-secrets** (dev) e **Secret do
  Kubernetes** (`conexao-solidaria-secret`, criado a partir de `secret.example.yaml`; `smoke.ps1`
  valida sua existência antes do apply). Senha do gestor seed via env `Seed__Gestor__Senha` (`AD-17`).
- **Dados sensíveis** — CPF como **Value Object** com validação e **máscara** (`AD-15`).
- **Borda** — **security headers** (X-Content-Type-Options, Referrer-Policy, X-Frame-Options, CSP) +
  HSTS fora de Dev; **rate limiting** (auth 10/min, doação 30/min, global 100/min; 429 + `Retry-After`)
  no Gateway (`AD-18`).
- **Superfície reduzida** — APIs são `ClusterIP` e só recebem tráfego do Gateway, reforçado por
  **NetworkPolicy** default-deny; senhas de credenciais BCrypt.
- **Hardening de pods** — `runAsNonRoot`, `runAsUser 10001`, `readOnlyRootFilesystem`, `drop ALL`,
  seccomp RuntimeDefault (`AD-19`).

---

## 10. Matriz de rastreabilidade

Requisitos do desafio (`HACKATHON NETT.pdf`) → onde está implementado → evidência.
**Todos os obrigatórios e os 2 bônus estão cobertos.**

### Requisitos funcionais (RF)

| Requisito | Implementação | Evidência |
| --- | --- | --- |
| Autenticação JWT + RBAC (`GestorONG`/`Doador`) | `Identity.Api` (JWT) + policies no Gateway/APIs | `AD-16`; testes de Identity |
| CRUD de campanha (regra `DataFim` futura + meta > 0) | `Campaigns.Api` + domínio `Campaign` | `CampaignRuleTests` |
| Cadastro de doador (email único, CPF válido, senha BCrypt) | `Identity.Api` + VO `Cpf` | `IdentityApiTests`, `CpfValidatorTests` |
| Transparência pública (só Ativa; título/meta/total) | `GET /api/campanhas/transparencia` + `/transparencia` | `funcionalidades.md` V3 |
| Doação (doador logado; bloqueia encerrada/cancelada) | `POST /api/doacoes` + `Donation.CanReceiveDonation` | `AsyncFlowTests` |

### Requisitos técnicos (RT) — obrigatórios

| Requisito | Implementação | Evidência |
| --- | --- | --- |
| ≥ 2 microsserviços | `Identity.Api`, `Campaigns.Api`, `Donations.Worker`, `Gateway` | `src/` |
| Processamento **assíncrono** (API publica, Worker consome; não atualiza direto) | Outbox + RabbitMQ + Worker | `AsyncFlowTests`; `AD-05..AD-09` |
| Kubernetes (`.yaml`) | Kustomize `infra/k8s/` (base + overlay local) | Deploy validado (seção 8) |
| Observabilidade (`/health` + `/metrics` + dashboard Grafana real) | ServiceDefaults + prometheus-net + Grafana | `infra/grafana/`; `AD-20` |
| CI/CD (build + imagem no push) | `.github/workflows/ci.yml` | Jobs `quality/tests/containers/publish` |

### Bônus (ambos feitos)

| Bônus | Implementação | Evidência |
| --- | --- | --- |
| Testes unitários no CI | Job `tests` (unit + integração + coverage) | `.github/workflows/ci.yml` |
| API Gateway | `ConexaoSolidaria.Gateway` (YARP) | `AD-04` |

### Entregáveis

README passo-a-passo, diagrama (`docs/arquitetura.md`) e PDF de justificativa dos bancos
(`docs/justificativa-bancos.md`) — todos presentes.

---

## 11. Limitações e dívidas técnicas conscientes

Registro honesto do que **não** está feito ou foi deliberadamente adiado:

- **MessagePack transitivo do Aspire (advisory)** — o grafo transitivo do Aspire arrasta uma versão de
  MessagePack com vulnerabilidade reportada. Afeta **só o tooling de dev** (não vai para as imagens de
  produção); atualização **pendente** até o Aspire publicar a correção transitiva (`AD-01`).
- **KEDA (autoscale do Worker)** — hoje o HPA escala os stateless por **CPU**; autoescalar o Worker por
  **profundidade de fila** (KEDA) é TODO (`AD-19`, `AD-33`).
- **Web multi-réplica** — o Blazor Server fica em `replicas: 1`. Multi-réplica exige **sticky sessions**
  (cookie de afinidade, já anotado no Ingress) e **Data Protection keys** em volume **RWX**
  compartilhado; preparado, mas não ativado (`AD-03`).
- **initContainer para migração** — o schema-wait dos apps (~30s) é curto; recomendação de initContainer
  que aguarda a migração ainda não implementada (mitigado por backoff de restart) — achado do deploy (`AD-28`).
- **MassTransit adiado** — mensageria mantida artesanal; migração faseada planejada para o 2º/3º evento
  de integração ou necessidade de saga (`AD-24`).
- **Pagamento real não implementado** — a doação é uma **intenção**; não há integração com gateway de
  pagamento. O fluxo modela recebimento/processamento, não a transação financeira.
- **Egress ainda aberto** no k8s; hardening de egress é TODO (`AD-19`).
- **Retenção de `processed_messages`** — a tabela de dedup cresce; política de limpeza/retenção é futura (`AD-06`).
- **Fallback de busca menos rico** — resultados via Postgres não têm fuzzy/scoring do ES; só campanhas
  criadas após a integração são indexadas (`AD-12`).
- **Output cache por instância** — 5s de defasagem adicional e cache não distribuído (por réplica) (`AD-31`).

---

## 12. Próximos passos

Do backlog do plano e das ideias de produto:

- **Pagamento real** — integrar gateway de pagamento e transformar a intenção de doação em transação
  efetiva (saga pagamento + notificação — candidato natural a adotar MassTransit, `AD-24`).
- **Doação recorrente** — assinaturas/recorrência com agendamento.
- **Relatórios** — exportação e relatórios agregados para gestores (evoluindo o read model `campaign_stats`).
- **Autoscale do Worker por fila (KEDA)** — escalar o consumo por profundidade de `doacoes-recebidas`.
- **Web multi-réplica** — ativar sticky sessions + Data Protection RWX para escalar o Blazor Server.
- **initContainer de espera de schema** — robustez do boot no k8s.
- **Hardening de egress** e **política de retenção** de `processed_messages`.
- **Adoção de MassTransit** quando surgir o 2º/3º evento de integração ou a necessidade de saga.

---

_Documento gerado como síntese técnica da entrega. Para o "porquê" detalhado de cada decisão, ver
`docs/decisoes-arquiteturais.md` (`AD-01..AD-35`); para o contrato HTTP, `docs/api-reference.md`;
para o catálogo por persona, `docs/funcionalidades.md`._
