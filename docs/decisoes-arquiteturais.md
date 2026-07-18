# Decisões Arquiteturais (ADR-lite)

Registro enxuto das decisões-chave do **Conexão Solidária**, agrupadas por tema.
Cada bloco segue o formato **Contexto → Decisão → Consequências/trade-offs**, com
foco honesto nos custos assumidos. Complementa `docs/arquitetura.md` (visão geral),
`docs/justificativa-bancos.md` (escolha de bancos) e os READMEs de Kubernetes e
Observabilidade.

Convenção: cada decisão é numerada (`AD-NN`) apenas para referência cruzada; não há
processo formal de supersessão — quando uma decisão substituiu outra, isso está dito
no próprio bloco.

---

## 1. Orquestração e execução

### AD-01 — .NET Aspire para orquestração local
- **Contexto:** a solução tem 5 processos (Identity, Campaigns, Worker, Gateway, Web)
  mais Postgres, RabbitMQ e Elasticsearch. Subir tudo à mão em dev é caro e propenso a erro.
- **Decisão:** usar **.NET Aspire** (`ConexaoSolidaria.AppHost`) para orquestrar o
  ambiente local com um comando, injetar connection strings/descoberta de serviço e
  publicar telemetria no **Aspire Dashboard**.
- **Consequências:**
  - (+) `dotnet run` no AppHost sobe o grafo inteiro; wiring de dependências centralizado.
  - (+) OTLP para o Aspire Dashboard sem configuração extra em dev.
  - (−) Aspire é para dev/local; o deploy real usa Docker Compose e Kubernetes/Kustomize
    (dois caminhos a manter em sincronia).
  - (−) **Dívida de segurança conhecida:** o grafo transitivo do Aspire arrasta uma
    versão de **MessagePack** com vulnerabilidade reportada. Só afeta o tooling de dev
    (não vai para as imagens de produção), mas a atualização fica **pendente** até o
    Aspire publicar a correção transitiva.

### AD-02 — ServiceDefaults compartilhado
- **Contexto:** health checks, OpenTelemetry e resiliência HTTP precisavam ser idênticos
  em todos os serviços.
- **Decisão:** projeto `ConexaoSolidaria.ServiceDefaults` com `MapDefaultEndpoints`
  (`/health`, `/alive`) e `AddOpenTelemetryExporters` (OTLP condicionado a
  `OTEL_EXPORTER_OTLP_ENDPOINT`).
- **Consequências:** (+) padrão único de observabilidade e probes; (+) as probes do
  Kubernetes reutilizam `/alive` e `/health`. (−) acoplamento de todos os serviços a um
  pacote comum (aceitável para um monólito distribuído pequeno).

---

## 2. Frontend

### AD-03 — Blazor Web App (Interactive Server) + MudBlazor
- **Contexto:** front administrativo/vitrine em C#, sem introduzir cadeia Node/npm no build.
- **Decisão:** **Blazor Web App** com render mode **Interactive Server** e componentes
  **MudBlazor**. Sem bundler JS próprio.
- **Consequências:**
  - (+) uma única linguagem/stack; produtividade de UI com MudBlazor pronto.
  - (−) **Circuitos SignalR:** o estado da UI vive no servidor por conexão. Perda de
    circuito = re-render; latência de rede impacta a interatividade.
  - (−) **Escala horizontal difícil:** o Web fica em `replicas: 1`. Multi-réplica exige
    (1) **sticky sessions** no Ingress (cookie de afinidade, já anotado em `ingress.yaml`)
    e (2) **Data Protection keys** em volume compartilhado **RWX** (`DataProtection__KeysPath`),
    porque o JWT é guardado via `ProtectedLocalStorage`. Fica como TODO.

---

## 3. Gateway / borda

### AD-04 — YARP como ponto de entrada único
- **Contexto:** duas APIs internas (Identity, Campaigns) não deveriam ser expostas
  diretamente; era preciso um ponto único para políticas de borda.
- **Decisão:** **YARP** (`ConexaoSolidaria.Gateway`) como reverse proxy único de `/api/*`,
  com **rate limiting**, **security headers** e **Correlation-Id** por requisição.
- **Consequências:**
  - (+) superfície externa reduzida; APIs ficam ClusterIP e só recebem tráfego do Gateway
    (reforçado por NetworkPolicy).
  - (+) rate limiting e headers de segurança num só lugar; correlação de logs ponta a ponta.
  - (−) mais um hop de rede e um ponto de escala (mitigado por HPA no Gateway).
  - Nota: os nomes de Service (`identity-api`, `campaigns-api`, `gateway`) batem com os
    hosts lógicos usados no código para o service discovery funcionar por DNS.

---

## 4. Mensageria confiável

### AD-05 — Outbox Pattern (evitar dual-write)
- **Contexto:** ao registrar uma doação era preciso persistir no Postgres **e** publicar
  um evento no RabbitMQ. Fazer os dois direto gera *dual-write* (um pode falhar após o outro).
- **Decisão:** **Outbox** — a doação e o evento são gravados na mesma transação do banco;
  um **OutboxDispatcher** lê a tabela e publica no broker de forma assíncrona.
- **Consequências:** (+) atomicidade real entre estado e evento; nenhum evento se perde por
  falha de broker no momento do request. (−) entrega **at-least-once** e latência extra
  (o consumidor precisa ser idempotente — ver AD-06). Métrica `conexao_outbox_pending_messages`
  observa o backlog; alerta dispara se > 20 por 2min.

### AD-06 — Idempotência por EventId
- **Contexto:** com at-least-once + retry/redelivery, a mesma mensagem pode chegar mais de uma vez.
- **Decisão:** deduplicação no consumidor por **EventId**, registrando processados em
  `processed_messages`; redeliveries já vistas são descartadas.
- **Consequências:** (+) reprocesso seguro; nenhuma doação contabilizada em dobro.
  (−) tabela de dedup cresce (precisa de retenção/limpeza no futuro).

### AD-07 — Retry escalonado + Dead-Letter Queue
- **Contexto:** falhas transitórias (broker/DB indisponível) não devem descartar a doação;
  falhas permanentes não podem travar a fila.
- **Decisão:** **retry escalonado** no consumo e, esgotadas as tentativas (3), envio para a
  **DLQ** `doacoes.dead-letter`.
- **Consequências:** (+) resiliência a falhas transitórias; isolamento de "poison messages".
  (−) mensagens em DLQ exigem intervenção manual (ver `docs/runbook.md`). Observado por
  `conexao_dead_letter_messages` (alerta crítico em > 0) e pelo Zabbix (profundidade da DLQ
  via RabbitMQ Management API).

### AD-08 — Evento versionado (SchemaVersion + CorrelationId)
- **Contexto:** o contrato de evento entre Campaigns e Worker vai evoluir.
- **Decisão:** o evento carrega **SchemaVersion** e **CorrelationId**.
- **Consequências:** (+) evolução de schema sem quebra e rastreabilidade ponta a ponta.
  (−) consumidor precisa tratar versões conhecidas explicitamente.

### AD-09 — Incremento atômico do arrecadado (evitar lost update)
- **Contexto:** vários eventos de doação podem atualizar o total arrecadado da mesma campanha
  concorrentemente; um read-modify-write clássico perde atualizações.
- **Decisão:** incremento **atômico** no banco via `ExecuteUpdateAsync` (UPDATE ... SET valor = valor + @x).
- **Consequências:** (+) sem *lost update*, sem lock pessimista na aplicação.
  (−) a regra fica em SQL/EF em vez de no modelo de domínio em memória (trade-off consciente
  por correção sob concorrência).

---

## 5. Persistência

### AD-10 — Modelo de domínio unificado no Shared + DbContext único
- **Contexto:** antes existiam **dois modelos** sobre as **mesmas tabelas** (API e Worker
  mapeavam entidades distintas), fonte de divergência e bugs.
- **Decisão:** modelo de domínio **unificado** em `ConexaoSolidaria.Shared` e um único
  **`CampaignsDbContext`** compartilhado por Campaigns.Api e Donations.Worker.
- **Consequências:** (+) uma fonte de verdade para o schema; fim das divergências.
  (−) acoplamento de API e Worker ao mesmo contexto/assembly (aceitável dado o domínio comum).

### AD-11 — EF Migrations (substitui EnsureCreated)
- **Contexto:** o `EnsureCreated` não versiona schema e, com dois contexts no mesmo banco,
  tinha comportamento **tudo-ou-nada** (o primeiro contexto a rodar criava as tabelas e o
  segundo, vendo o banco já existente, não criava o que faltava).
- **Decisão:** schema gerido por **EF Migrations**, aplicadas em boot com `MigrateAsync`.
- **Consequências:** (+) schema versionado e evolutivo; migração determinística no startup.
  (−) **exige banco limpo/compatível**: aplicar migrations sobre um banco criado por
  `EnsureCreated` (sem `__EFMigrationsHistory`) falha — em ambiente local, recriar o volume.
  (−) o boot depende do banco; por isso a `livenessProbe` usa `/alive` (não depende de DB)
  e a `readinessProbe` usa `/health`, para não reiniciar o pod em cascata durante o Migrate.

### AD-12 — Fallback de busca Elasticsearch → Postgres
- **Contexto:** a busca de campanhas usa Elasticsearch (relevância, fuzzy e multi-campo — `AD-35`),
  mas o ES pode estar indisponível ou sem índice.
- **Decisão:** **fallback** — se o ES falhar/indisponível, a busca cai para o Postgres
  (`ILIKE` em título/descrição), com a mesma paginação e formato de resposta.
- **Consequências:** (+) disponibilidade da busca mesmo sem ES; degradação graciosa.
  (−) resultados do fallback são **bem menos ricos**: `ILIKE` é substring literal — sem
  fuzzy/scoring, sem tolerância a acento e sem busca por categoria (um typo simplesmente não
  retorna nada). O gap de "só campanhas criadas após a integração são indexadas" foi **fechado
  pelo backfill** do `AD-35`: o Postgres é a fonte da verdade e o índice é reconstruível.

### AD-23 — `Campaigns.Api` e `Donations.Worker` são um único bounded context (dois processos)
- **Contexto:** `Campaigns.Api` e `Donations.Worker` **compartilham o mesmo banco
  (`campaignsdb`) e o mesmo `CampaignsDbContext`** (ver AD-10). Isso, isolado, parece uma
  violação clássica de microsserviços ("dois serviços não compartilham banco"). A pergunta
  legítima é: por que não separar? A resposta está na fronteira de domínio, não na topologia
  de deploy.
- **Decisão:** tratar API e Worker como **um único bounded context — Doações/Campanhas —
  deployado como dois processos** (o processo de escrita/consulta HTTP e o processo consumidor
  de eventos), e **não** como dois microsserviços independentes. Ambos falam a mesma linguagem
  ubíqua (Campanha, Doação, arrecadado), operam as mesmas invariantes (ex.: incremento atômico
  do arrecadado — AD-09) e evoluem juntos. Por isso compartilham deliberadamente o modelo de
  domínio (`ConexaoSolidaria.Shared`) e o `CampaignsDbContext`. A separação em dois processos é
  uma decisão de **escalabilidade/isolamento operacional** (a ingestão assíncrona de doações
  escala e falha independentemente da API), não de **decomposição de domínio**.
- **Consequências:**
  - (+) **sem duplicação de schema/mapeamento**: uma única fonte de verdade para as tabelas de
    campanha/doação; fim das divergências que existiam com dois modelos (AD-10).
  - (+) o acoplamento entre API e Worker é **intencional e explícito**, coerente com o domínio
    compartilhado — não um vazamento acidental.
  - (+) o deploy em dois processos preserva o benefício real (escala/isolamento do consumo de
    fila) sem pagar o custo de sincronizar dois modelos.
  - (−) **trade-off / dívida latente:** se um dia Doações precisar evoluir para um serviço
    verdadeiramente independente (ciclo de release próprio, banco próprio, time próprio), o
    Worker deverá **ganhar um modelo/DbContext próprios** e a integração passará a ser só por
    contrato de evento (AD-08), sem banco compartilhado. Hoje esse custo **não** se justifica:
    o domínio é um só e a duplicação seria pura cerimônia.
  - **Reflexo operacional (B8):** por compartilharem o `CampaignsDbContext`, **um único** Job de
    migração (`campaigns-migrations`) cobre o schema dos dois processos, e ambos os deployments
    (API e Worker) sobem com `Migrations__RunOnStartup=false` para não competir pelo
    `MigrateAsync` (ver AD-11 e `infra/k8s/base/migrations-job.yaml`).

---

## 6. API / contrato

### AD-13 — ProblemDetails + status HTTP corretos
- **Contexto:** respostas de erro inconsistentes dificultam o consumo pelo front e testes.
- **Decisão:** **ProblemDetails** (RFC 7807) e status semânticos: **422** (validação de negócio),
  **409** (conflito, ex.: idempotência), **404** (não encontrado).
- **Consequências:** (+) contrato de erro previsível e padronizado. (−) exige disciplina para
  mapear cada caso ao status certo.

### AD-14 — Idempotency-Key na doação
- **Contexto:** o cliente pode reenviar o POST de doação (timeout/retry) e gerar duplicata.
- **Decisão:** header **`Idempotency-Key`** na criação de doação; requisições repetidas com a
  mesma chave não criam nova doação (retornam o resultado anterior / 409).
- **Consequências:** (+) proteção contra doação em duplicidade na borda (complementa a
  idempotência de consumo do AD-06). (−) cliente precisa gerar e reenviar a chave.

### AD-15 — Value Object de CPF
- **Contexto:** CPF aparece em cadastro de doador e precisa de validação/normalização consistente.
- **Decisão:** **Value Object de CPF** encapsulando validação e formato.
- **Consequências:** (+) regra de CPF num só lugar, imutável e testável. (−) mais um tipo de
  domínio a mapear no EF.

### AD-16 — Policies de autorização nomeadas
- **Contexto:** endpoints com regras distintas por papel (GestorONG, Doador).
- **Decisão:** **policies nomeadas** aplicadas por endpoint.
- **Consequências:** (+) autorização declarativa e legível. (−) exige manter o catálogo de
  policies alinhado aos papéis semeados.

---

## 7. Segurança

### AD-17 — Segredos fora do Git
- **Contexto:** senhas de Postgres/RabbitMQ/Grafana/Zabbix, JWT secret e senha do gestor
  semeado não podem ser versionados.
- **Decisão:** segredos via **`.env`** (Compose), **user-secrets** (dev) e **Secret do
  Kubernetes** (`conexao-solidaria-secret`, criado a partir de `secret.example.yaml`;
  `secret.yaml` é gitignored).
- **Consequências:** (+) nenhum segredo real no repositório; ver `SECURITY.md`.
  (−) passo manual de preencher e aplicar o Secret antes do deploy (o `smoke.ps1` valida
  a existência do Secret antes do apply).

### AD-18 — Rate limiting e security headers no Gateway
- **Contexto:** APIs expostas via borda precisam de proteção contra abuso e headers de segurança.
- **Decisão:** **rate limiting** + **security headers** aplicados no YARP Gateway (ver AD-04).
- **Consequências:** (+) proteção centralizada na entrada. (−) limites mal calibrados podem
  barrar tráfego legítimo em picos (ajustável).

### AD-19 — Hardening do Kubernetes
- **Contexto:** o deploy precisava sair de um manifesto monolítico com `emptyDir` e NodePorts
  abertos para algo mais próximo de produção.
- **Decisão:** **Kustomize** (base + overlay `local`) com: **StatefulSet + PVC** para
  Postgres/RabbitMQ e PVC dedicado para ES; **tudo ClusterIP** com **Ingress nginx** como
  entrada única (sticky cookie p/ Blazor); **probes** (`startup`/`readiness`/`liveness`);
  **requests/limits**; **securityContext** (`runAsNonRoot`, `runAsUser 10001`,
  `readOnlyRootFilesystem`, `drop ALL`, `seccomp RuntimeDefault`, `emptyDir` em `/tmp`);
  **11 NetworkPolicies** (default-deny + allow-list); **HPA** por CPU; **PDB**.
- **Consequências:** (+) postura de segurança e resiliência muito superior ao manifesto antigo.
  (−) mais complexidade operacional; **egresso** ainda aberto (hardening de egress é TODO);
  **KEDA** para autoescalar o Worker por profundidade de fila é TODO (hoje HPA só por CPU nos
  stateless). Detalhes em `infra/k8s/README.md` e `ReadmeKubernetes.md`.

---

## 8. Observabilidade

### AD-20 — OpenTelemetry + prometheus-net + Prometheus/Grafana
- **Contexto:** era preciso enxergar negócio, aplicação e mensageria em tempo quase real.
- **Decisão:** **OTel** via ServiceDefaults (OTLP → Aspire Dashboard local / Collector opcional)
  e **prometheus-net** expondo `/metrics` em todos os serviços; **Prometheus** faz scrape e
  **Grafana** provisiona 3 dashboards (`negocio`, `aplicacao`, `mensageria`) + alertas.
- **Consequências:** (+) métricas HTTP (`http_requests_received_total`,
  `http_request_duration_seconds_*`) e de negócio (`conexao_*`) unificadas; alertas de DLQ,
  outbox e 5xx. (−) dois caminhos de telemetria (OTLP e Prometheus scrape) coexistindo;
  o **OTel Collector** (`infra/otel/`) só é acionado quando `OTEL_EXPORTER_OTLP_ENDPOINT`
  está definido. Detalhes em `ReadmeObservabilidade.md`.

### AD-21 — Zabbix para disponibilidade (template real)
- **Contexto:** além de métricas de app, era preciso monitoração clássica de disponibilidade
  e profundidade de fila, importável por quem opera.
- **Decisão:** **template Zabbix real** (`infra/zabbix/templates/conexao-solidaria-template.yaml`):
  web monitoring do `/health` das 3 APIs + latência, vitrine pública de transparência, e
  profundidade da fila principal e da DLQ via **RabbitMQ Management API**; 9 triggers.
- **Consequências:** (+) monitoração de disponibilidade independente do Prometheus, importável
  pela UI. (−) sobreposição parcial com os alertas do Grafana (DLQ/fila são cobertas nos dois);
  macros de RabbitMQ precisam ser preenchidas por host.

---

## 9. Testes

### AD-22 — Testes de integração com Testcontainers
- **Contexto:** o fluxo crítico (doação → outbox → broker → worker → arrecadado) só é confiável
  se testado contra Postgres/RabbitMQ reais, não mocks.
- **Decisão:** **testes de integração com Testcontainers** subindo dependências reais em contêiner.
- **Consequências:** (+) cobre Outbox, idempotência, retry/DLQ e incremento atômico de ponta a
  ponta com infra real. (−) mais lentos que testes unitários e exigem Docker disponível no
  ambiente de CI/dev.

## 10. Evolução avaliada e adiada

### AD-24 — Manter mensageria artesanal em vez de adotar MassTransit (por ora)
- **Contexto:** hoje o Outbox transacional, o dispatcher com publisher confirms, a idempotência
  (dedup por `EventId` em `processed_messages`), o retry escalonado (10s/60s) e a DLQ são
  implementados manualmente sobre o `RabbitMQ.Client`. O **MassTransit** entregaria muito disso
  pronto (EF Outbox/Inbox, retry/redelivery, filas de erro, sagas, versionamento de mensagem).
- **Decisão:** **avaliado e adiado.** Mantemos a implementação artesanal atual. Motivos:
  (1) ela **funciona e está coberta por 12 testes de integração** contra Postgres/RabbitMQ reais;
  (2) o MassTransit troca o substrato inteiro (envelope de mensagem próprio, tabelas de outbox/inbox
  próprias — `OutboxState`/`InboxState`, filas `<fila>_error`/`_skipped`), o que **quebraria e exigiria
  reescrever** esses testes e a topologia (`doacoes-recebidas`, `doacoes.retry.*`, `doacoes.dead-letter`);
  (3) para o escopo atual (um único evento de integração) o ganho é baixo e o risco de desestabilizar
  um fluxo já validado é alto; (4) a versão explícita **demonstra melhor os conceitos** (Outbox, at-least-once,
  idempotência) na avaliação de arquitetura e no vídeo.
- **Plano de migração (quando crescer):** adotar MassTransit ao surgir o **2º/3º evento de integração**
  ou a necessidade de **saga** (ex.: pagamento + notificação). Migração faseada: (a) introduzir o
  MassTransit lado a lado configurando o **EF Outbox** apontando ao mesmo `campaignsdb`; (b) mover o
  publisher da Campaigns.Api para `IPublishEndpoint`; (c) converter o `DonationConsumerWorker` em um
  `IConsumer<DoacaoRecebidaEvent>` usando retry/redelivery/error-queue nativos; (d) reescrever os
  testes de integração para os artefatos do MassTransit; (e) remover o dispatcher/consumidor artesanais.
- **Consequências:** (+) preserva um núcleo testado e estável agora; (−) mantém código de infraestrutura
  de mensageria que uma biblioteca madura eliminaria — dívida técnica assumida e documentada.

---

## 11. Refino de arquitetura e novas funcionalidades (AD-25+)

Bloco das decisões mais recentes: a fatia B (refatorações de acoplamento/performance)
e as 5 funcionalidades de usuário. Complementa `docs/funcionalidades.md` (catálogo por
persona) e `docs/api-reference.md` (contrato HTTP).

### AD-25 — Projeto `Contracts` puro (desacoplar Identity do EF)
- **Contexto:** o `ConexaoSolidaria.Identity.Api` só precisa de tipos leves — papéis
  (`ApplicationRoles`), opções de JWT, o Value Object de CPF e o contrato dos eventos/mensageria.
  Antes, para reaproveitar esses tipos, ele referenciava `ConexaoSolidaria.Shared`, que carrega
  **EF Core, Npgsql, migrations e o `CampaignsDbContext`**. Resultado: o Identity arrastava
  transitivamente todo o stack de persistência de campanhas/doações que ele **nunca usa**
  (o Identity tem o próprio `IdentityDbContext`).
- **Decisão:** extrair um projeto **`ConexaoSolidaria.Contracts`** com **tipos puros, sem
  dependência de EF**: `Auth/ApplicationRoles`, `Auth/JwtOptions`, `Events/DoacaoRecebidaEvent`,
  `Events/DoacaoProcessadaNotification`, `ValueObjects/Cpf`, `Validation/CpfValidator`,
  `Messaging/RabbitMqOptions` e `RabbitMqConnectionFactoryBuilder`. O Identity passa a referenciar
  **somente `Contracts`**; `Shared` continua sendo a casa do domínio EF (campanhas/doações).
- **Consequências:**
  - (+) Identity com superfície de dependências mínima: build mais enxuto, imagem menor, sem
    arrastar EF/Npgsql que ele não usa.
  - (+) o contrato de integração (evento/notificação, papéis, CPF) fica num assembly neutro que
    **todos** os serviços podem referenciar sem herdar persistência — o mesmo `DoacaoRecebidaEvent`
    é publicado pela Campaigns.Api e consumido pelo Worker, e a `DoacaoProcessadaNotification` é
    produzida pelo Worker e consumida pelo Web, todos via `Contracts`.
  - (+) reforça a fronteira: EF é detalhe de `Shared`, não de contrato público.
  - (−) mais um projeto na solução e um pequeno julgamento recorrente ("isto é contrato puro ou
    domínio com persistência?") ao adicionar tipos. Os Dockerfiles das imagens precisaram ser
    ajustados para incluir o novo projeto no contexto de build.

### AD-26 — Read model `campaign_stats` (CQRS leve, escrito pelo Worker, lido por `/stats`)
- **Contexto:** dashboards de gestão/transparência avançada precisam de agregados por campanha
  (total arrecadado, nº de doações processadas, última atualização). Calcular isso a cada request
  (contar/​somar `donations` com join em `campaigns`) é caro sob carga e mistura a carga de leitura
  analítica com o caminho de escrita transacional.
- **Decisão:** manter uma **tabela de leitura desnormalizada `campaign_stats`** (`CampaignId`,
  `Titulo`, `MetaFinanceira`, `TotalArrecadado`, `DoacoesProcessadas`, `AtualizadoEm`). Ela é
  **escrita exclusivamente pelo `Donations.Worker`** via **UPSERT atômico** (`INSERT … ON CONFLICT
  DO UPDATE`, `TotalArrecadado += valor`, `DoacoesProcessadas += 1`) na **mesma transação** que
  incrementa `campaigns.ValorTotalArrecadado` e grava `processed_messages`. A leitura
  (`GET /api/campanhas/stats`) apenas projeta a tabela com `AsNoTracking`.
- **Consequências:**
  - (+) leitura de dashboard O(linhas de campanha), sem agregação em runtime; separa o modelo de
    leitura do de escrita (CQRS pragmático) usando o **mesmo banco** (não introduz outro store).
  - (+) por ser atualizado na transação do Worker, é **consistente com o total da campanha** — não
    há janela em que `campaign_stats` e `campaigns` divirjam por causa de uma doação processada.
  - (−) **duplicação deliberada**: `TotalArrecadado` existe em `campaigns` **e** em `campaign_stats`.
    É redundância assumida em troca da separação leitura/escrita e do custo de agregação. A coerência
    depende de o UPSERT viver na mesma transação do incremento (se um caminho de escrita esquecer
    disso, o read model diverge).
  - (−) só reflete doações **processadas** (não pendentes) e só cobre campanhas que já receberam ao
    menos uma doação processada — dashboards precisam tratar campanhas ainda ausentes da tabela.
  - Cross-ref: incremento atômico (AD-09), idempotência por EventId (AD-06), bounded context
    único API+Worker (AD-23).

### AD-27 — Versionamento de API por header/query (sem versionar a rota)
- **Contexto:** o contrato HTTP vai evoluir, mas as rotas atuais (`/api/auth/*`,
  `/api/campanhas/*`, `/api/doacoes/*`) já são consumidas pelo Web e pelo Gateway. Versionar por
  segmento de URL (`/api/v1/...`) quebraria essas rotas e a configuração do YARP.
- **Decisão:** adotar **Asp.Versioning** com leitura de versão por **header `x-api-version`** e/ou
  **query `api-version`** (`ApiVersionReader.Combine(HeaderApiVersionReader, QueryStringApiVersionReader)`),
  **default `1.0`** e `AssumeDefaultVersionWhenUnspecified`. `SubstituteApiVersionInUrl = false`:
  a versão **não** entra na rota. `ReportApiVersions = true` para anunciar versões suportadas via
  header de resposta.
- **Consequências:**
  - (+) as rotas existentes seguem **inalteradas**; clientes atuais não precisam mudar nada (a
    ausência de versão assume 1.0).
  - (+) evolução futura sem reescrever rotas nem tocar no roteamento do Gateway — basta anotar
    controllers/actions com novas `ApiVersion` e os clientes pedirem via header/query.
  - (−) a versão fica "invisível" na URL (menos óbvia em logs/curl do que `/v1/`); quem depura
    precisa lembrar de inspecionar o header/query.
  - (−) exige que os dois serviços (Identity e Campaigns) mantenham a **mesma convenção** de reader
    e default para não haver surpresa entre eles.

### AD-28 — Migração de schema como Job dedicado no k8s (um migrador por banco)
- **Contexto:** com **EF Migrations** aplicadas em boot (AD-11) e **dois processos** compartilhando
  o `campaignsdb` (Campaigns.Api + Donations.Worker — AD-23), deixar cada réplica/pod rodar
  `MigrateAsync` no startup gera **corrida pelo lock de migração** e acopla a disponibilidade do app
  ao tempo de migração.
- **Decisão:** separar migração de execução com **dois modos por serviço**: flag de configuração
  **`Migrations:RunOnStartup`** (deployments sobem com `Migrations__RunOnStartup=false`) e um modo
  **`RunMigrationsOnly=true`** que só aplica as migrations e encerra. No Kubernetes, **Jobs de
  migração dedicados** (`identity-migrations`, `campaigns-migrations`) rodam o modo `RunMigrationsOnly`
  antes dos deployments servirem tráfego. **Um único Job por banco**: como API e Worker compartilham
  o `CampaignsDbContext`, um só `campaigns-migrations` cobre o schema dos dois (nenhum dos dois migra
  no startup).
- **Consequências:**
  - (+) schema aplicado **uma vez**, de forma determinística, por um Job — sem corrida entre réplicas
    e sem acoplar readiness do app ao Migrate.
  - (+) rollback/replay do schema é uma operação de Job isolada, observável (`Complete`/`Failed`).
  - (−/achado do deploy ao vivo) **NetworkPolicy:** com default-deny, os Jobs de migração
    (`app=*-migrations`) precisaram de uma regra explícita para acessar o Postgres — **sem isso o
    schema não nasce** e os apps entram em CrashLoop esperando tabelas que nunca chegam. A allow-list
    `migrations→postgres` é obrigatória.
  - (−/achado do deploy ao vivo) **timing do schema-wait:** o loop de espera de schema dos apps
    (~30s) é curto; se Postgres/Job de migração demorarem, os pods podem entrar em CrashLoop antes de
    o schema existir. Recomendação registrada: um **initContainer** que aguarda a migração concluir
    (hoje mitiga-se com o backoff de restart do próprio pod).
  - Cross-ref: AD-11 (Migrations vs EnsureCreated), AD-23 (um bounded context, dois processos, um
    migrador).

### AD-29 — Tracing distribuído com OTel Collector + Grafana Tempo
- **Contexto:** o OTLP dos serviços (AD-20) vai direto ao **Aspire Dashboard** em dev, mas em
  Compose/k8s não há Aspire. Faltava um coletor central e um backend de **traces** persistente para
  correlacionar o fluxo assíncrono (request → outbox → broker → worker → notificação) fora do dev.
- **Decisão:** introduzir um **OpenTelemetry Collector** (`infra/otel/`) como destino do OTLP quando
  `OTEL_EXPORTER_OTLP_ENDPOINT` está definido; o Collector encaminha **traces para o Grafana Tempo**
  (datasource no Grafana) e mantém métricas no Prometheus. A propagação de contexto (`traceparent`)
  já atravessa o HTTP (ServiceDefaults) e o RabbitMQ (headers do evento), então o trace segue a
  doação de ponta a ponta.
- **Consequências:**
  - (+) trace ponta a ponta visível no Grafana (Tempo) em Compose/k8s, incluindo o salto assíncrono
    pela fila; correlação com métricas e logs no mesmo Grafana.
  - (+) desacopla o app do backend de tracing: trocar Tempo por outro backend é config do Collector,
    não dos serviços.
  - (−) mais um componente de infra para operar (o Collector) e **dois caminhos de telemetria**
    coexistindo (OTLP→Collector→Tempo para traces; scrape Prometheus para métricas — AD-20).
  - (−) só é acionado quando o endpoint OTLP está configurado; em dev puro o caminho é o Aspire
    Dashboard, então há duas topologias de observabilidade a manter em mente.

### AD-30 — Notificações em tempo real (fanout dedicado + consumer resiliente no Web, polling como fallback)
- **Contexto:** após uma doação ser processada de forma assíncrona pelo Worker, a UI do doador só
  descobria o novo status por **polling** do `GET /api/doacoes/{id}`. Queríamos atualização
  **em tempo real** sem transformar o polling em dependência dura.
- **Decisão:** o `Donations.Worker`, **só no sucesso** e de forma **best-effort**, publica uma
  `DoacaoProcessadaNotification` num **exchange fanout dedicado** `conexao-solidaria.notifications`
  (separado do fluxo transacional de doações). O `ConexaoSolidaria.Web` roda um `NotificationConsumer`
  (BackgroundService singleton) que declara o fanout, cria uma **fila anônima exclusiva/auto-delete**
  (uma por réplica → todas as réplicas recebem cópia), consome com **autoAck** e reemite via
  `NotificationDispatcher` para as telas Blazor. O consumer é **resiliente**: nunca lança para fora
  do `ExecuteAsync`; em falha de broker apenas loga e **reconecta com backoff exponencial (5s→60s)**.
  Enquanto o broker estiver fora, **o polling de status permanece como fallback**.
- **Consequências:**
  - (+) feedback em tempo real por cima de um fallback confiável — a UI nunca fica "presa" no broker.
  - (+) exchange **separado** do fluxo de doações: notificação é descartável e não interfere no
    Outbox/retry/DLQ (AD-05/07). `autoAck` + fila auto-delete significam "nada a reprocessar" se o Web
    cair — o estado real vem sempre do backend.
  - (−) **acopla o frontend ao RabbitMQ**: o Web passa a ser um consumidor do broker (e a referenciar
    a mensageria/`Contracts`), o que aumenta a superfície do frontend e exige NetworkPolicy
    `web→rabbitmq` no k8s. Trade-off consciente: o acoplamento é opcional (degradável para polling).
  - (−) entrega **best-effort/at-most-once** para a notificação (autoAck, sem retry): uma notificação
    perdida não corrige o estado — por isso o polling continua sendo a fonte de verdade da UI.
  - (−) multi-réplica do Web recebe N cópias (uma por fila anônima); aceitável porque cada réplica só
    atualiza os circuitos Blazor que ela hospeda.

### AD-31 — Output caching nos reads públicos anônimos
- **Contexto:** `search`, `transparencia` e `stats` são endpoints **anônimos** de alta leitura e
  **eventualmente consistentes** (o total vem do Worker de forma assíncrona). Servir cada request
  direto do banco/ES sob carga é desperdício, já que uma pequena defasagem é aceitável.
- **Decisão:** aplicar **Output Caching** com **janela curta (~5s)** (`[OutputCache(Duration = 5)]`)
  em `GET /api/campanhas/search`, `GET /api/campanhas/transparencia` e `GET /api/campanhas/stats`.
- **Consequências:**
  - (+) reduz pressão no Postgres/Elasticsearch em picos de leitura pública; respostas mais rápidas.
  - (+) a janela de 5s casa com a natureza eventualmente consistente do total arrecadado (já defasado
    pelo processamento assíncrono) — não introduz inconsistência perceptível a mais.
  - (−) até 5s de **defasagem adicional** após uma doação ser processada; leituras logo após um
    processamento podem mostrar o valor anterior. Aceitável para transparência/dashboard, **não** para
    o caminho de escrita (o POST de doação e a consulta de status por dono **não** são cacheados).
  - (−) o cache é por instância (não distribuído); com várias réplicas cada uma tem seu próprio cache
    de 5s.

### AD-32 — EF `EnableRetryOnFailure` + execution strategy explícita no Worker
- **Contexto:** falhas **transitórias** de conexão/comando no Postgres (restart, failover, blip de
  rede) não deveriam derrubar o processamento. O EF oferece retry automático via
  `EnableRetryOnFailure`, mas isso instala uma **execution strategy** que **proíbe** transações
  iniciadas pelo usuário fora de um bloco `ExecuteAsync` (a estratégia precisa poder reexecutar o
  bloco inteiro).
- **Decisão:** habilitar **`EnableRetryOnFailure`** no Npgsql de todos os serviços que falam com o
  banco. No `Donations.Worker`, onde há uma **transação multi-statement** (incremento em `campaigns`
  + UPSERT em `campaign_stats` + insert em `processed_messages`), envolver a transação inteira em
  **`db.Database.CreateExecutionStrategy().ExecuteAsync(...)`**, para que um retry reexecute o bloco
  atômico completo.
- **Consequências:**
  - (+) resiliência a falhas transitórias de banco sem intervenção; o Worker se recupera de blips.
  - (+) a `ExecuteAsync` garante que o retry **reexecuta a transação toda** (tudo-ou-nada): não há
    risco de reincrementar `campaigns` sem reescrever `campaign_stats`.
  - (−) exige disciplina: **qualquer** transação explícita no código precisa ir dentro da execution
    strategy, senão o EF lança em runtime. É um custo cognitivo recorrente.
  - (−) retries mascaram latência: sob instabilidade, o processamento fica mais lento (por design)
    antes de falhar de vez.
  - Cross-ref: incremento atômico (AD-09), read model na mesma transação (AD-26), idempotência (AD-06)
    — o retry é seguro **porque** o consumo é idempotente por EventId.

### AD-33 — Prefetch (QoS) do consumidor de doações
- **Contexto:** por padrão o RabbitMQ empurra mensagens ao consumidor sem limite de "em voo", o que
  pode inundar um consumidor lento; com prefetch 1, a vazão fica limitada pelo round-trip de ack.
- **Decisão:** configurar **`BasicQos` com `prefetchCount = 10`** (`global: false`) no
  `DonationConsumerWorker`: até 10 mensagens não confirmadas em processamento por vez.
- **Consequências:**
  - (+) equilíbrio entre **vazão** (pipeline de até 10) e **backpressure** (não puxa trabalho
    ilimitado); melhora throughput sem estourar memória do consumidor.
  - (+) combina com a idempotência (AD-06) e o ack manual: mensagens não confirmadas voltam à fila se
    o worker cair, sem duplicar efeito.
  - (−) valor fixo (10), não adaptativo; sob perfis de carga muito diferentes pode precisar de tuning.
    Autoescalar o Worker por profundidade de fila (KEDA) segue como TODO (AD-19).

### AD-34 — Circuit breaker (Polly) no fallback de busca Elasticsearch → Postgres
- **Contexto:** o fallback ES→Postgres (AD-12) já cobre indisponibilidade do ES, mas **cada** busca
  ainda tentava o ES primeiro. Com o ES fora/lento, isso significa pagar timeout a cada request antes
  de cair no Postgres — desperdício e latência.
- **Decisão:** proteger a chamada ao Elasticsearch com um **circuit breaker do Polly**
  (`ResiliencePipeline` nomeado `elasticsearch-search`). Após N falhas o circuito **abre** e as buscas
  passam a lançar `BrokenCircuitException` **imediatamente**, caindo **direto no fallback do Postgres**
  sem tocar o ES por um período; depois o circuito testa a recuperação (half-open).
- **Consequências:**
  - (+) enquanto o ES está fora, as buscas vão direto ao Postgres **sem pagar timeout** repetido —
    latência estável e ES não é "martelado" enquanto se recupera.
  - (+) recuperação automática (half-open) sem intervenção; o breaker complementa o fallback já
    existente (AD-12) em vez de substituí-lo.
  - (−) resultados do fallback continuam **menos ricos** (sem fuzzy/scoring); com o circuito aberto,
    **toda** busca é degradada por um tempo, mesmo que o ES já tenha voltado (até o half-open testar).
  - (−) mais um pipeline de resiliência a configurar/observar (limiares de falha e tempo de abertura).

### AD-35 — Índice explícito com analisador pt-BR, busca fuzzy multi-campo e backfill
- **Contexto:** o índice `campanhas` nascia por **mapeamento dinâmico** do ES, no primeiro
  `IndexAsync`. Consequência: analisador `standard` como padrão — **sem tratamento de acento**
  ("saude" não encontrava "Saúde"), sem stemming pt-BR e sem stopwords. A query era um
  `multi_match` com `fuzziness: AUTO` restrito a `titulo^3`/`descricao`: categoria não era
  pesquisável, não havia busca por prefixo (autocomplete) e a relevância não distinguia frase
  exata de match espalhado. Além disso, o índice só continha campanhas **criadas após** a
  integração (não havia backfill) e cada busca fazia **duas** idas ao ES (`Count` + `Search`,
  com a query duplicada nos dois lugares).
- **Decisão:**
  - **Criar o índice explicitamente** (`EnsureIndexAsync`, idempotente) com análise pt-BR:
    `asciifolding` (ignora acento nos dois sentidos), stemmer `light_portuguese`, stopwords
    `_portuguese_`, e um subcampo `titulo.prefix` com `edge_ngram` para autocomplete. A definição
    é enviada como **JSON cru pelo transporte low-level** — o DSL tipado de `analysis` é verboso
    e frágil entre versões do client, e o JSON espelha a documentação do ES.
  - **Query composta** (`bool/should` com `minimum_should_match: 1`) somando quatro sinais
    complementares: `multi_match best_fields` com `fuzziness AUTO` (absorve typos),
    `multi_match phrase_prefix` (busca conforme digita), `match` em `titulo.prefix`
    (autocomplete por prefixo de palavra) e `match_phrase` no título com boost 5 (frase exata
    rankeia no topo). Campos pesquisados: **título (peso 3) + descrição + categoria**
    (`categoriaTexto`, rótulo humano — "Meio Ambiente" não sairia do enum `MeioAmbiente`, que o
    tokenizer trata como um token só).
  - **Enums como string** no `_source` (`JsonStringEnumConverter` no source serializer), para
    mapear `status`/`categoria` como `keyword` (filtro exato) e lê-los de volta corretamente.
  - **Backfill automático no startup** (`ElasticsearchIndexInitializer`): quando `EnsureIndexAsync`
    **cria** o índice, reindexa em bulk todas as campanhas do Postgres. Best-effort — falha apenas
    loga (o ES não é crítico; a busca degrada para o Postgres).
  - **Remover a query `Count` duplicada**, usando `TrackTotalHits(true)` + `response.Total`.
- **Consequências:**
  - (+) busca tolerante a **erro de digitação, acento e plural**, sobre título/descrição/categoria;
    autocomplete por prefixo; frase exata no topo do ranking.
  - (+) o índice deixa de ser efeito colateral do primeiro write: é **reconstruível** a partir do
    Postgres (fonte da verdade) — basta dropar o índice e reiniciar a API.
  - (+) metade das idas ao ES por busca (uma query em vez de duas).
  - (−) **o mapeamento não é retroativo**: o ES não permite trocar o analisador de um campo já
    criado e `EnsureIndexAsync` **só cria se não existir**. Aplicar mudança de mapeamento exige
    **dropar o índice e reiniciar** a `campaigns-api`, deixando o backfill repopular — ver
    **Cenário 9** do runbook. Não há alias + reindex versionado (custo aceito nesta POC).
  - (−) `asciifolding` com `preserve_original` e o subcampo `edge_ngram` incham o índice;
    irrelevante nesta escala, relevante se o volume crescer.
  - (−) os testes de integração usam `FakeCampaignSearchRepository`, então **mapeamento e query
    reais não têm cobertura automatizada**; a validação foi feita contra um Elasticsearch 8.15.3
    real (typo, sem acento, stemming, prefixo e categoria).

### AD-36 — Imagens em registry público (Docker Hub) + auto-update no cluster com Keel
- **Contexto:** o Kubernetes do Docker Desktop roda num node `kind` com **containerd próprio**,
  separado do daemon do Docker. Como as imagens só existiam localmente (`conexao-solidaria/<svc>:local`,
  sem registry), era preciso **exportá-las e importá-las manualmente** no node
  (`docker save | ctr -n k8s.io images import`) a cada build. Isso tinha três problemas: (1) passo
  manual esquecível — a causa nº 1 de "meu fix não subiu"; (2) com `imagePullPolicy: IfNotPresent`,
  reusar a **mesma tag** fazia o kubelet manter a imagem antiga, forçando o hack de inventar tags
  novas a cada alteração (`catv1`, `catv8`); (3) nenhum caminho de entrega contínua até o cluster.
- **Decisão:** publicar as 5 imagens da aplicação num **registry público** —
  **Docker Hub** `junonn5/conexao-solidaria-<svc>:latest` — via `infra/k8s/push-dockerhub.ps1`
  (login com PAT por `--password-stdin`, token nunca versionado), e apontar o overlay local para
  lá (`newName` + `newTag` no bloco `images:`) com **`imagePullPolicy: Always`**. Para fechar o
  ciclo, instalar o **Keel** (`infra/k8s/keel/keel.yaml`, namespace `keel`) como operador de
  auto-update: os 5 Deployments recebem `keel.sh/policy: force`, `keel.sh/trigger: poll` e
  `keel.sh/pollSchedule: "@every 1m"`. Repositórios **públicos**, então o Keel puxa sem
  `imagePullSecret`. O `up.ps1` instala o Keel e o passo de `ctr import` foi **removido**.
  Coexistência de registries proposital: o **CI publica no GHCR** (tags `:sha` + `:latest`,
  rastreabilidade por commit) e o **ambiente local consome o Docker Hub**.
- **Consequências:**
  - (+) elimina o passo manual de import no node e a classe inteira de bug "pod rodando imagem antiga".
  - (+) **entrega contínua até o cluster local**: republicar a imagem basta — o Keel detecta a
    mudança de digest de `:latest` em ~1 min e recria os pods, sem `apply` nem `rollout restart`.
  - (+) some a gambiarra de tags incrementais (`catv1`/`catv8`): `:latest` mutável + digest resolve.
  - (+) as imagens ficam reproduzíveis fora da máquina do dev (qualquer cluster com internet sobe a stack).
  - (−) passa a exigir **rede** e um registry disponível; sem internet o cluster não sobe do zero
    (o cache do containerd cobre reinícios, não um deploy limpo).
  - (−) repositórios **públicos** expõem os artefatos da POC; para código privado seria preciso
    repos privados + `imagePullSecret` no namespace e credenciais para o Keel.
  - (−) `:latest` é uma tag **mutável** — bom para dev/demo, inadequado para produção, onde o certo
    é tag imutável por commit (`:sha`) com política `keel.sh/policy: minor|patch` sobre semver.
  - (−) mais um componente no cluster (Keel) com RBAC de `update` em Deployments do cluster inteiro.
  - Cross-ref: AD-19 (hardening do k8s), AD-28 (Jobs de migração). Detalhes operacionais em
    `ReadmeKubernetes.md` (seções 2, 3, 5 e 10) e `infra/k8s/README.md`.
