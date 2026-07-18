# Referência de API — Conexão Solidária

Contrato HTTP dos serviços, **sempre via Gateway** (`ConexaoSolidaria.Gateway`, YARP — `AD-04`).
O Gateway roteia:

- `/api/auth/*` → **Identity.Api**
- `/api/campanhas/*` e `/api/doacoes/*` → **Campaigns.Api**

Complementa `docs/funcionalidades.md` (o que cada endpoint entrega, por persona) e
`docs/decisoes-arquiteturais.md` (`AD-NN`). Os *shapes* abaixo refletem os DTOs reais
(`Requests/*`, `Responses/*`) e as entidades de `ConexaoSolidaria.Shared.Domain`.

> Base URL local (k8s): Gateway em `http://localhost:18080` — port-forward que o
> `infra/k8s/up.ps1` já deixa ativo. (Alternativa sem port-forward: NodePort do overlay local em
> `http://localhost:30080`, que exige o nginx ingress controller por causa da NetworkPolicy.)
> Todas as rotas são relativas a essa base.

---

## Convenções gerais

### Autenticação
- **JWT Bearer** emitido pelo Identity (`POST /api/auth/login` ou `/cadastro-doador`).
- Enviar em `Authorization: Bearer <accessToken>`.
- Papéis (claim `role`): **`Doador`** e **`GestorONG`**. Autorização por **policies nomeadas**
  (`AD-16`): `CampaignManagement` (GestorONG), `DonationCreation` (Doador).
- Coluna **Auth** de cada endpoint: `anon` (anônimo), `Doador` ou `GestorONG`.

### Versionamento (`AD-27`)
- Versão por **header `x-api-version`** e/ou **query `api-version`**. Ex.: `x-api-version: 1.0` ou
  `?api-version=1.0`.
- **Default `1.0`** quando omitida (`AssumeDefaultVersionWhenUnspecified`). A versão **não** entra
  na rota — as rotas atuais são estáveis.
- Respostas anunciam versões suportadas (`api-supported-versions`, `ReportApiVersions = true`).

### Erros — ProblemDetails (RFC 7807, `AD-13`)
Todos os erros retornam `application/problem+json`:

| Status | Quando |
|---|---|
| **401 Unauthorized** | sem token / token inválido / token sem identificador do doador |
| **403 Forbidden** | autenticado mas sem a policy/papel exigido |
| **404 Not Found** | recurso inexistente (ou doação de outro doador — não vaza existência) |
| **409 Conflict** | conflito (email/CPF já cadastrado; corrida de idempotência) |
| **422 Unprocessable Entity** | validação de negócio / regra de domínio (`DomainRuleException`) |
| **429 Too Many Requests** | rate limit do Gateway (`AD-18`); inclui `Retry-After` |

Rate limits do Gateway: auth **10/min**, doações **30/min**, global **100/min por IP**.

Shape típico de erro de validação (422):

```json
{
  "type": "https://tools.ietf.org/html/rfc4918#section-11.2",
  "title": "Unprocessable Entity",
  "status": 422,
  "errors": { "cpf": ["CPF invalido."], "senha": ["Senha deve conter pelo menos 8 caracteres."] }
}
```

---

## Identity — `/api/auth`

### POST `/api/auth/cadastro-doador` — Auth: `anon`
Cria conta de doador e já retorna o token.

Request:
```json
{
  "nomeCompleto": "Maria Silva",
  "email": "maria@exemplo.com",
  "cpf": "529.982.247-25",
  "senha": "senhaForte123"
}
```
- Validações: `nomeCompleto` obrigatório; `email` válido e **único**; `cpf` **válido** (Value
  Object, `AD-15`) e **único**; `senha` ≥ **8** caracteres (armazenada com **BCrypt**).

Response **201 Created** (`AuthResponse`):
```json
{
  "usuarioId": "0192f4c8-....",
  "nomeCompleto": "Maria Silva",
  "email": "maria@exemplo.com",
  "role": "Doador",
  "accessToken": "eyJhbGciOi...",
  "expiraEm": "2026-07-13T18:30:00+00:00"
}
```
- Erros: **422** (validação de campos), **409** (email ou CPF já cadastrado).

### POST `/api/auth/login` — Auth: `anon`
Autentica e retorna o token.

Request:
```json
{ "email": "maria@exemplo.com", "senha": "senhaForte123" }
```

Response **200 OK** (`AuthResponse` — mesmo shape do cadastro).
- Erros: **422** (email/senha ausentes), **401** (credenciais inválidas — BCrypt não confere).

### GET `/api/auth/me` — Auth: qualquer autenticado
Retorna os claims do usuário logado.

Response **200 OK**:
```json
{
  "usuarioId": "0192f4c8-....",
  "nome": "Maria Silva",
  "email": "maria@exemplo.com",
  "role": "Doador"
}
```
- Erros: **401** (sem token).

---

## Campanhas — `/api/campanhas`

Enum **`CampaignStatus`**: `Ativa`, `Concluida`, `Cancelada` — serializado/aceito como **string**
(as APIs usam `JsonStringEnumConverter`). `DonationStatus` idem: `Pendente`, `Processada`, `Rejeitada`, `Falha`.

### GET `/api/campanhas/search` — Auth: `anon`
Busca paginada por termo (ES com fallback Postgres — `AD-12`/`AD-34`). **Output cache ~5s**
(`AD-31`).

O termo é buscado em **título (peso 3), descrição e categoria**, com analisador pt-BR e query
fuzzy multi-campo (`AD-35`) — os resultados vêm ordenados por **relevância**:

| Comportamento | Exemplo |
|---|---|
| Corrige erro de digitação (`fuzziness AUTO`) | `cadera gamer` → "Cadeira Gamer" |
| Ignora acento (nos dois sentidos) | `sao paulo` ↔ "São Paulo"; `saude` ↔ "Saúde" |
| Plural/derivação (stemming pt-BR) | `metrica` → "Metricas Demo Julho" |
| Prefixo / autocomplete (`edge_ngram`) | `comput` → "Comprar computador" |
| Busca por categoria | `ambiente` → campanhas de "Meio Ambiente" |
| Frase exata no título rankeia no topo | `match_phrase` com boost 5 |

Query: `q` (termo; **vazio → lista todas** as campanhas do Postgres, paginadas, da mais recente
para a mais antiga — não consulta o ES), `page` (default 1), `pageSize` (default 10, máx 100).

> Com o ES indisponível a busca degrada para o Postgres (`ILIKE` substring): os resultados seguem
> corretos, porém **sem** fuzzy, acento-insensível ou categoria (`AD-12`).

Response **200 OK** (`PaginatedResponse<CampanhaResponse>`):
```json
{
  "items": [
    {
      "id": "0192...",
      "titulo": "Cestas de inverno",
      "descricao": "Arrecadação para cestas básicas",
      "dataInicio": "2026-06-01T00:00:00+00:00",
      "dataFim": "2026-08-01T00:00:00+00:00",
      "metaFinanceira": 10000.00,
      "valorTotalArrecadado": 3250.00,
      "status": "Ativa"
    }
  ],
  "page": 1,
  "pageSize": 10,
  "total": 1,
  "totalPages": 1
}
```

### GET `/api/campanhas/{id}` — Auth: `anon` (público)
Detalhe de uma campanha por Id (`AD` melhoria A1). Token, se presente, não bloqueia.

Response **200 OK** (`CampanhaResponse` — mesmo shape do item acima).
- Erros: **404** (campanha não encontrada).

### GET `/api/campanhas/transparencia` — Auth: `anon`
Campanhas **Ativas e vigentes** (`Status == Ativa && DataFim >= agora`), ordenadas por `DataFim`.
**Output cache ~5s** (`AD-31`).

Response **200 OK** (`TransparenciaCampanhaResponse[]`):
```json
[
  { "titulo": "Cestas de inverno", "metaFinanceira": 10000.00, "valorTotalArrecadado": 3250.00 }
]
```

### GET `/api/campanhas/stats` — Auth: `anon`
Read model agregado `campaign_stats` (escrito pelo Worker — `AD-26`). **Output cache ~5s**.

Response **200 OK** (`CampanhaStatsResponse[]`, ordenado por `atualizadoEm` desc):
```json
[
  {
    "campaignId": "0192...",
    "titulo": "Cestas de inverno",
    "metaFinanceira": 10000.00,
    "totalArrecadado": 3250.00,
    "doacoesProcessadas": 13,
    "atualizadoEm": "2026-07-13T18:20:00+00:00"
  }
]
```

### POST `/api/campanhas` — Auth: `GestorONG` (policy `CampaignManagement`)
Cria campanha.

Request (`SalvarCampanhaRequest`):
```json
{
  "titulo": "Cestas de inverno",
  "descricao": "Arrecadação para cestas básicas",
  "dataInicio": "2026-06-01T00:00:00+00:00",
  "dataFim": "2026-08-01T00:00:00+00:00",
  "metaFinanceira": 10000.00,
  "status": "Ativa"
}
```
- Regras de domínio: título/descrição obrigatórios; `dataFim` não no passado e `>= dataInicio`;
  `metaFinanceira > 0`.

Response **201 Created** (`CampanhaResponse`), `Location` → `GET /api/campanhas/{id}`.
- Erros: **422** (regra de domínio), **401/403** (sem token / sem papel Gestor).

### PUT `/api/campanhas/{id}` — Auth: `GestorONG`
Atualiza campanha. Se `status` mudar, aplica as **transições de domínio** (não seta valor cru).

Request: `SalvarCampanhaRequest` (mesmo shape do POST).

Response **200 OK** (`CampanhaResponse`).
- Erros: **404** (não encontrada), **422** (validação/transição inválida).

### POST `/api/campanhas/{id}/ativar` · `/concluir` · `/cancelar` — Auth: `GestorONG`
Ações de ciclo de vida. Transições válidas: **`Ativa → Concluida`** e **`Ativa → Cancelada`**;
Concluída/Cancelada são **terminais**; mesmo estado é no-op idempotente.

Request: sem corpo.

Response **200 OK** (`CampanhaResponse` com o novo `status`).
- Erros: **404** (não encontrada), **422** (transição inválida, ex.: `Concluida → Ativa`).

---

## Doações — `/api/doacoes`

Enum **`DonationStatus`** (serializado como **string** nas respostas): `Pendente`, `Processada`,
`Rejeitada`, `Falha`.

### POST `/api/doacoes` — Auth: `Doador` (policy `DonationCreation`)
Registra uma doação — **assíncrona** (Outbox `AD-05`). Aceita header opcional
**`Idempotency-Key`** (`AD-14`).

Headers: `Authorization: Bearer ...`; opcional `Idempotency-Key: <uuid-do-cliente>`.

Request (`CriarDoacaoRequest`):
```json
{ "idCampanha": "0192...", "valorDoacao": 50.00 }
```

Response **202 Accepted** (`DoacaoAceitaResponse`):
```json
{
  "doacaoId": "0193...",
  "campanhaId": "0192...",
  "valorDoacao": 50.00,
  "status": "Pendente",
  "mensagem": "Doacao recebida e enviada para processamento assincrono."
}
```
- `Idempotency-Key` repetida devolve a **mesma** doação (202), sem recriar.
- Erros: **401** (token sem id do doador), **404** (campanha inexistente), **422**
  (`valorDoacao <= 0`, ou campanha encerrada/cancelada — `CanReceiveDonation` falso).

### GET `/api/doacoes/{id}` — Auth: `Doador`
Status enriquecido (comprovante/recibo) — com título e datas da campanha.

Response **200 OK** (`DoacaoStatusResponse`):
```json
{
  "doacaoId": "0193...",
  "campanhaId": "0192...",
  "valorDoacao": 50.00,
  "status": "Processada",
  "campanhaTitulo": "Cestas de inverno",
  "criadaEm": "2026-07-13T18:19:57+00:00",
  "processadaEm": "2026-07-13T18:20:00+00:00"
}
```
- `processadaEm` é **null** enquanto `status == Pendente`.
- Erros: **401** (token sem id), **404** (inexistente **ou de outro doador** — não vaza existência).

### GET `/api/doacoes/minhas` — Auth: `Doador`
Histórico do doador autenticado, da mais recente para a mais antiga.

Response **200 OK** (`MinhaDoacaoResponse[]`):
```json
[
  {
    "doacaoId": "0193...",
    "campanhaId": "0192...",
    "campanhaTitulo": "Cestas de inverno",
    "valorDoacao": 50.00,
    "status": "Processada",
    "criadaEm": "2026-07-13T18:19:57+00:00",
    "processadaEm": "2026-07-13T18:20:00+00:00"
  }
]
```
- Erros: **401** (token sem id do doador).

---

## Notificação de doação processada (tempo real, `AD-30`)

**Não é endpoint HTTP.** Após processar a doação com sucesso, o `Donations.Worker` publica
`DoacaoProcessadaNotification` no **fanout `conexao-solidaria.notifications`** (best-effort). O
`ConexaoSolidaria.Web` consome (fila anônima, resiliente) e atualiza as telas Blazor em tempo real.
Se o broker estiver fora, o **polling de `GET /api/doacoes/{id}` é o fallback**.

Shape do payload (`Contracts/Events/DoacaoProcessadaNotification`):
```json
{
  "doacaoId": "0193...",
  "campanhaId": "0192...",
  "campanhaTitulo": "Cestas de inverno",
  "valor": 50.00,
  "totalArrecadado": 3300.00,
  "metaFinanceira": 10000.00,
  "metaAtingida": false,
  "processadaEm": "2026-07-13T18:20:00+00:00"
}
```

---

## Exemplo de fluxo end-to-end

1. **Login do gestor**
   ```http
   POST /api/auth/login
   { "email": "gestor@ong.org", "senha": "..." }
   → 200 { accessToken, role: "GestorONG", ... }
   ```
2. **Criar e ativar campanha** (Bearer do gestor)
   ```http
   POST /api/campanhas
   { "titulo": "...", "descricao": "...", "dataInicio": "...", "dataFim": "...", "metaFinanceira": 10000, "status": "Ativa" }
   → 201 { id: "0192...", status: 1 }
   # se criada em rascunho, publicar:
   POST /api/campanhas/0192.../ativar → 200 { status: 1 }
   ```
3. **Cadastro/login do doador**
   ```http
   POST /api/auth/cadastro-doador
   { "nomeCompleto": "...", "email": "...", "cpf": "529.982.247-25", "senha": "senhaForte123" }
   → 201 { accessToken, role: "Doador", ... }
   ```
4. **Doar** (Bearer do doador, com idempotência)
   ```http
   POST /api/doacoes
   Idempotency-Key: 6f1c...-cliente
   { "idCampanha": "0192...", "valorDoacao": 50.00 }
   → 202 { doacaoId: "0193...", status: "Pendente" }
   ```
5. **Acompanhar** (polling do status; ou receber a notificação em tempo real)
   ```http
   GET /api/doacoes/0193...
   → 200 { status: "Pendente", processadaEm: null }
   # após o Worker processar (~segundos):
   → 200 { status: "Processada", processadaEm: "2026-07-13T18:20:00+00:00" }
   ```
6. **Ver histórico / transparência**
   ```http
   GET /api/doacoes/minhas        → 200 [ { ..., status: "Processada" } ]
   GET /api/campanhas/stats       → 200 [ { totalArrecadado: 3300.00, doacoesProcessadas: 14 } ]
   ```
