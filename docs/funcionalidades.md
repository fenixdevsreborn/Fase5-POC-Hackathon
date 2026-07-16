# Funcionalidades — Conexão Solidária

Catálogo de funcionalidades **por persona**, com o que cada uma faz, a **rota/endpoint**
envolvido e o **encaixe arquitetural**. Complementa `docs/api-reference.md` (contrato HTTP
detalhado) e `docs/decisoes-arquiteturais.md` (o "porquê" de cada decisão, `AD-NN`).

Todo acesso externo passa pelo **Gateway YARP** (`AD-04`): rotas de UI (`/campanhas`,
`/doador`, ...) são páginas do Blazor (`ConexaoSolidaria.Web`); rotas `/api/*` são as APIs
(`Identity`, `Campaigns`) atrás do Gateway.

## Personas

- **Visitante** — anônimo, sem login. Explora a vitrine e a transparência.
- **Doador** — usuário autenticado com papel `Doador`. Doa e acompanha suas doações.
- **GestorONG** — usuário autenticado com papel `GestorONG`. Administra campanhas.

Autorização por **policies nomeadas** (`AD-16`): `CampaignManagement` (GestorONG) e
`DonationCreation` (Doador). Papéis vêm no JWT emitido pelo Identity.

---

## Visitante (anônimo)

### V1 — Explorar/buscar campanhas
- **O que faz:** busca campanhas por termo em **título, descrição e categoria**, paginada e
  ordenada por relevância. **Tolera erro de digitação e acento**: `cadera gamer` acha
  "Cadeira Gamer", `sao paulo` acha "São Paulo", `metrica` acha "Metricas" (plural/stemming) e
  `comput` acha "Comprar computador" (prefixo/autocomplete).
- **UI / endpoint:** página `/campanhas` → `GET /api/campanhas/search?q=&page=&pageSize=`
  (anônimo).
- **Encaixe arquitetural:** busca no **Elasticsearch** com analisador pt-BR e query fuzzy
  multi-campo (`AD-35`), com **fallback para Postgres** (`AD-12`) protegido por **circuit
  breaker** (`AD-34`); resposta com **output cache ~5s** (`AD-31`). Sem termo (`q` vazio) **não**
  vai ao ES: lista todas as campanhas direto do Postgres, paginadas, da mais recente para a mais
  antiga (é o que alimenta a vitrine e o painel do gestor).

### V2 — Ver detalhe público de uma campanha
- **O que faz:** consulta uma campanha específica por Id, sem precisar listar/filtrar.
- **UI / endpoint:** página `/campanhas/{id}` → `GET /api/campanhas/{id}` (anônimo/público —
  melhoria A1). O token, se presente, não bloqueia.
- **Encaixe arquitetural:** projeção direta `AsNoTracking` para o DTO público, sem materializar
  a entidade rica.

### V3 — Painel de transparência
- **O que faz:** lista campanhas **Ativas e vigentes** com título, meta e total arrecadado.
- **UI / endpoint:** página `/transparencia` → `GET /api/campanhas/transparencia` (anônimo).
- **Encaixe arquitetural:** filtra `Status == Ativa && DataFim >= agora`, ordena por `DataFim`;
  **output cache ~5s** (`AD-31`). Eventualmente consistente: o total é atualizado pelo Worker de
  forma assíncrona.

### V4 — Estatísticas agregadas (read model)
- **O que faz:** expõe agregados por campanha (meta, total arrecadado, nº de doações
  processadas, última atualização) para dashboards/transparência avançada.
- **UI / endpoint:** `GET /api/campanhas/stats` (anônimo).
- **Encaixe arquitetural:** lê o **read model CQRS `campaign_stats`** (`AD-26`), tabela
  **escrita pelo Worker** em UPSERT atômico; a API só projeta (`AsNoTracking`). **Output cache
  ~5s** (`AD-31`).

### V5 — Cadastro e login
- **O que faz:** criar conta de doador e autenticar.
- **UI / endpoint:** páginas `/cadastrar` e `/entrar` →
  `POST /api/auth/cadastro-doador` (201) e `POST /api/auth/login` (200).
- **Encaixe arquitetural:** `Identity.Api` valida (email único, **CPF válido** via Value Object
  `AD-15`, senha ≥ 8 chars **BCrypt**), emite **JWT** com papel `Doador`. Erros como
  **ProblemDetails** (`AD-13`): 422 validação, 409 conflito de email/CPF. O Web guarda o token em
  `ProtectedLocalStorage` com `AuthenticationStateProvider` custom.

---

## Doador (autenticado, policy `DonationCreation`)

Inclui tudo do Visitante, mais:

### D1 — Doar (assíncrono, com status)
- **O que faz:** registra uma doação para uma campanha; recebe **202 Accepted** e acompanha o
  processamento assíncrono.
- **UI / endpoint:** fluxo de doação (a partir de `/campanhas/{id}`) →
  `POST /api/doacoes` (202, policy `DonationCreation`). Consulta de status:
  `GET /api/doacoes/{id}` (200).
- **Encaixe arquitetural:** o POST valida a campanha (`CanReceiveDonation`: bloqueia
  encerrada/cancelada → 422), grava **doação + evento (Outbox) + idempotency key na mesma
  transação** (`AD-05`), responde 202. O **Worker** consome de forma **idempotente** (dedup por
  EventId, `AD-06`), incrementa o total **atomicamente** (`AD-09`), atualiza `campaign_stats`
  (`AD-26`) e publica a notificação de conclusão. Status evolui `Pendente → Processada`
  (ou `Rejeitada`/`Falha`).

### D2 — Valores rápidos na doação (funcionalidade nova #5)
- **O que faz:** botões de valores sugeridos (ex.: R$ 25 / 50 / 100) para acelerar o preenchimento
  do valor da doação.
- **UI / endpoint:** UI da tela de doação; alimenta o mesmo `POST /api/doacoes`
  (`{ idCampanha, valorDoacao }`).
- **Encaixe arquitetural:** puramente de **UX no frontend** — não altera o contrato do endpoint;
  o `valorDoacao` continua validado (> 0) no backend.

### D3 — Idempotência da doação (proteção contra duplicidade)
- **O que faz:** reenvio do POST (timeout/retry do cliente) **não** cria doação duplicada.
- **UI / endpoint:** header **`Idempotency-Key`** no `POST /api/doacoes` (`AD-14`).
- **Encaixe arquitetural:** a chave é gravada na mesma transação da doação; reapresentação com a
  mesma chave devolve a **mesma** doação (202) sem recriar; corrida (unique violation) recarrega e
  devolve a doação vencedora. Complementa a idempotência de consumo do Worker (`AD-06`).

### D4 — Minhas doações (histórico) (funcionalidade nova #1)
- **O que faz:** lista as doações do doador autenticado, da mais recente para a mais antiga, com
  título da campanha, valor, status e datas.
- **UI / endpoint:** página `/doador/doacoes` → `GET /api/doacoes/minhas` (200, policy
  `DonationCreation`).
- **Encaixe arquitetural:** projeção `AsNoTracking` filtrando por `DoadorId` (do JWT), com join na
  campanha para o título; índice `donations(DoadorId)` para performance.

### D5 — Comprovante/recibo da doação (funcionalidade nova #2)
- **O que faz:** exibe o comprovante de uma doação (título da campanha, valor, status, data de
  criação e de processamento).
- **UI / endpoint:** página `/doador/doacoes/{id}` → `GET /api/doacoes/{id}` (status enriquecido
  com título + datas).
- **Encaixe arquitetural:** a projeção faz **join com a campanha** para trazer o título e as datas
  do comprovante. **404 também quando a doação pertence a outro doador** — não revela existência de
  recursos alheios.

### D6 — Notificações em tempo real (funcionalidade nova #4)
- **O que faz:** quando a doação termina de ser processada, a tela do doador atualiza **em tempo
  real**, sem esperar o polling.
- **UI / endpoint:** telas Blazor conectadas (SignalR interno do Blazor Server); sem endpoint HTTP
  público novo. Fonte: fanout `conexao-solidaria.notifications`.
- **Encaixe arquitetural:** o **Worker** publica `DoacaoProcessadaNotification` num **fanout
  dedicado** (best-effort, só no sucesso); o **`NotificationConsumer` do Web** (resiliente, fila
  anônima, reconexão com backoff) reemite para as telas (`AD-30`). Se o broker estiver fora, o
  **polling de `GET /api/doacoes/{id}` é o fallback** — a UI nunca depende do broker.

---

## GestorONG (autenticado, policy `CampaignManagement`)

### G1 — Dashboard do gestor
- **O que faz:** ponto de entrada de gestão das campanhas.
- **UI / endpoint:** página `/gestor` (consome os endpoints de campanha abaixo).
- **Encaixe arquitetural:** UI Blazor autenticada; chamadas via `ApiClient` tipado com resiliência
  herdada do ServiceDefaults.

### G2 — Criar campanha
- **O que faz:** cria uma campanha (título, descrição, datas, meta, status).
- **UI / endpoint:** página `/gestor/campanhas/nova` → `POST /api/campanhas` (201, policy
  `CampaignManagement`).
- **Encaixe arquitetural:** regras de domínio no `Campaign` (título/descrição obrigatórios,
  **DataFim não no passado e ≥ DataInicio**, **meta > 0**); `DomainRuleException` vira **422**
  global (`AD-13`).

### G3 — Editar campanha
- **O que faz:** atualiza os dados de uma campanha; mudança de status respeita as transições de
  domínio.
- **UI / endpoint:** página `/gestor/campanhas/{id}/editar` → `PUT /api/campanhas/{id}`.
- **Encaixe arquitetural:** se o status muda no update, aplica as **regras de transição** (não seta
  valor cru); 404 se não encontrada, 422 se transição/validação inválida.

### G4 — Ações de ciclo de vida da campanha (funcionalidade nova #3)
- **O que faz:** ativar, concluir ou cancelar uma campanha como ações explícitas (em vez de editar
  o campo status).
- **UI / endpoint:** botões no dashboard/edição →
  `POST /api/campanhas/{id}/ativar`, `.../concluir`, `.../cancelar` (200, policy
  `CampaignManagement`).
- **Encaixe arquitetural:** o **domínio valida a transição** (`TransitionTo`): **Ativa** é o único
  estado de origem que muda (`Ativa → Concluida` e `Ativa → Cancelada`); Concluída/Cancelada são
  **terminais**; transição para o mesmo estado é no-op idempotente. Transição inválida →
  `DomainRuleException` → **422**; 404 se a campanha não existe.

### G5 — Visão de transparência/estatísticas
- **O que faz:** o gestor também consome a transparência (V3) e os agregados (V4) para acompanhar
  arrecadação.
- **UI / endpoint:** `GET /api/campanhas/transparencia`, `GET /api/campanhas/stats`.
- **Encaixe arquitetural:** mesmos reads públicos cacheados (`AD-31`) e read model `campaign_stats`
  (`AD-26`).

---

## Resumo funcionalidade → rota → decisão

| # | Funcionalidade | Persona | Endpoint principal | Decisões |
|---|---|---|---|---|
| V1 | Buscar campanhas | Visitante | `GET /api/campanhas/search` | AD-35, AD-12, AD-34, AD-31 |
| V2 | Detalhe da campanha | Visitante | `GET /api/campanhas/{id}` | A1 |
| V3 | Transparência | Visitante | `GET /api/campanhas/transparencia` | AD-31 |
| V4 | Estatísticas (read model) | Visitante | `GET /api/campanhas/stats` | AD-26, AD-31 |
| V5 | Cadastro/login | Visitante | `POST /api/auth/cadastro-doador`, `/login` | AD-13, AD-15 |
| D1 | Doar (assíncrono + status) | Doador | `POST /api/doacoes`, `GET /api/doacoes/{id}` | AD-05, AD-06, AD-09 |
| D2 | Valores rápidos (#5) | Doador | `POST /api/doacoes` | (UX) |
| D3 | Idempotência da doação | Doador | `POST /api/doacoes` + `Idempotency-Key` | AD-14 |
| D4 | Minhas doações (#1) | Doador | `GET /api/doacoes/minhas` | AD-16 |
| D5 | Comprovante/recibo (#2) | Doador | `GET /api/doacoes/{id}` | — |
| D6 | Notificações tempo real (#4) | Doador | fanout `...notifications` | AD-30 |
| G2 | Criar campanha | GestorONG | `POST /api/campanhas` | AD-13, AD-16 |
| G3 | Editar campanha | GestorONG | `PUT /api/campanhas/{id}` | AD-16 |
| G4 | Ciclo de vida (#3) | GestorONG | `POST /api/campanhas/{id}/{ativar\|concluir\|cancelar}` | AD-13 |

---

## Ideias futuras (não implementadas)

Registradas como direção, **sem implementação** no projeto atual:

- **Doação recorrente** — assinatura mensal com agendamento; exigiria scheduler + novo evento de
  integração (bom gatilho para reavaliar MassTransit, `AD-24`).
- **Pagamento real (Pix/cartão)** — hoje a doação é registrada como intenção processada
  assincronamente, **sem gateway de pagamento**; integrar Pix/cartão traria uma **saga**
  (pagamento + confirmação + notificação).
- **Seguir campanha** — doador acompanha uma campanha e recebe atualizações; encaixaria no canal de
  notificações em tempo real já existente (`AD-30`).
- **Relatórios exportáveis** — exportar arrecadação/doações (CSV/PDF) para o gestor; consumiria o
  read model `campaign_stats` (`AD-26`).

> Todas as ideias acima são **não implementadas** — constam apenas como evolução planejada.
