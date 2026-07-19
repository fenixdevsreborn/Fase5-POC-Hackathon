# Landing cinematográfica — "O Laço" — Conexão Solidária

Brief de produção e plano de implementação para transformar a landing pública (`/`) numa
experiência contínua com **sequência de quadros controlada por rolagem**, mídia gerada no
**Magnific MCP** e sobreposição de texto HTML real.

História definida pelo usuário após três rodadas de propostas. Nome: **"O Laço"** — a caixa
bonita tem um laço, e a história é um laço: termina na mesma logo em que começou.

- **Alvo:** `src/ConexaoSolidaria.Web` (Blazor Web App, Interactive Server, MudBlazor)
- **Branch:** `feat/landing-cinematografica` (backup: tag `pre-landing-cinematografica`)
- **Resolução:** 1080p · **Modelo de vídeo:** Seedance 1.5 Pro (definido pelo usuário)

---

## 1. Origem e adaptação

Adaptado de um brief para outro projeto (site estático de marido de aluguel, mídia via
Higgsfield). Decisões de tradução que permanecem válidas:

- **Não consolidar o app numa página única** — existe JWT, RBAC, painel do gestor e SignalR;
  a landing `/` é a única superfície alterada. `/campanhas`, `/transparencia`, `/entrar`,
  `/cadastrar`, `/doador/*`, `/gestor/*` e `/status` intactas.
- **Sem identity reference** — não há pessoa real associada ao projeto; figuras brasileiras
  genéricas.
- **Conversão** = "Quero Doar" → `/campanhas` e "Criar Campanha" → `/cadastrar` (não WhatsApp).
- **Higgsfield/"Cinema Studio 3.0" não existem no Magnific** — modelos reais na seção 6.
- **1080p, não 4K** — os quadros são reamostrados para ~1440px no canvas; 4K dobraria o custo
  sem ganho visível (e estouraria o saldo).

O que se mantém do brief original: 5 clipes 16:9 de 5–10s **sem áudio**; fotorrealismo
publicitário; câmera lenta e estável; profundidade de campo; luz volumétrica; continuidade por
último-quadro → quadro-inicial; **nenhum texto/logo dentro da mídia gerada** (a logo tem
tratamento próprio — seção 5); canvas dirigido por scroll com GSAP + ScrollTrigger + Lenis;
fallback mobile e `prefers-reduced-motion`; verificação em navegador real antes de declarar
pronto.

---

## 2. A história — "O Laço"

A definição do usuário: abertura com a logo no notebook dissolvendo em luz; **somente a
segunda cena é tecnologia**; o restante é a doação chegando — uma criança e um adulto
recebendo uma **caixa bonita com a logo**; no final a cena **vira a logo de novo**, com a
**foto real da campanha**, e **fica em loop**.

| # | Clipe | Duração | O que acontece | Contrapartida arquitetural |
| --- | --- | --- | --- | --- |
| 1 | **A Logo que Vira Luz** | ~6s | Notebook na mesa, logomarca na tela; a logo se desfaz em partículas que mergulham para dentro da tela | `POST /api/doacoes` → `202` + Outbox |
| 2 | **A Máquina** | ~7s | O pulso corre fibras óticas → data center (racks azuis) → torre emite anéis de luz → pousa no centro comunitário | Gateway → RabbitMQ → Worker → notificação |
| 3 | **A Caixa** | ~6s | Mãos de voluntários fecham a caixa kraft premium com fita, logo e laço; a van parte no amanhecer | Doação processada, total atualizado |
| 4 | **A Chegada** | ~6s | Uma criança e um adulto recebem a caixa na porta e abrem juntos; alegria genuína, luz dourada | Impacto real |
| 5 | **O Laço Fecha** | ~6s | Close na logo da caixa; as partículas se desprendem e **a logo se re-forma em tela** — a mesma da abertura — com a foto real da campanha e o CTA | Transparência; o ciclo recomeça |

**O loop é duplo.** Narrativo: o filme termina na mesma logo em que começou — cada doação
recomeça o ciclo. Visual: a seção final tem animação ambiente **em loop infinito** (partículas
de luz orbitando a logo re-formada, via GSAP/CSS), com a foto da campanha e o CTA. O loop não
reinicia o scroll.

**Arco de cor:** frio → quente. Clipes 1–2 em azul-noite (`#071b3f`, luz `#2563eb`); o clipe 3
faz a virada dentro do plano (amanhecer na van); clipes 4–5 em dourado (`#f97316`/`#ff8a1f`).
A paleta é a do `brand.css` — a marca sempre conteve o arco.

---

## 3. Direção de arte

| Papel | Token / valor | Uso |
| --- | --- | --- |
| Noite / clipes 1–2 | `#071b3f` → `#082d56` (`--brand-primary`) | Base fria |
| Luz de tela e pulso | `#2563eb` (`--cs-blue`) | Fonte de luz dos clipes 1–2 |
| Virada / clipe 3 | `#f97316` (`--cs-orange`) | Amanhecer invadindo o azul |
| Luz solar / clipes 4–5 | `#ff8a1f` (`--brand-highlight`) | Key light quente |
| Confirmação | `#18a566` (`--brand-impact`) | Barras de meta (HTML) |
| Texto sobreposto | `#f6f9ff` branco quente | Títulos e leads |

**Fotografia:** luz sempre motivada (tela, LEDs de rack, anéis da torre, faróis, sol na
porta); poeira/névoa para o feixe existir; materiais táteis — madeira, kraft, fita, pele;
profundidade de campo rasa. Publicitário e acolhedor, nunca noir. **Pessoas:** brasileiras,
diversas, roupas cotidianas; expressões contidas e verdadeiras; nenhum rosto falando (sem
áudio). **A caixa:** kraft premium, fita azul da marca, logo aplicada, laço — o único objeto
que precisa permanecer idêntico entre os clipes 3, 4 e 5.

**Tipografia:** Inter (já no `App.razor`), 800 nos títulos, `letter-spacing: -0.035em`,
seguindo `.cs-hero__title`.

---

## 4. Roteiro e prompts

Todos: `aspectRatio: "16:9"`, `resolution: "1080p"`, sem `soundEffects`. O Seedance 1.5 Pro
**não tem `cameraMotion` estruturado** — a câmera é dirigida no texto — e **não aceita
`references[]`**: a consistência vem de **`keyframes.start` + `keyframes.end`** gerados no
Nano Banana Pro (seção 6).

### Negative prompt comum

```
deformed hands, extra fingers, distorted faces, asymmetric eyes, warped facial features,
uncanny expressions, floating objects, morphing geometry, warped logo, illegible label, text,
letters, watermark, brand name, signage, subtitles, UI overlay, readable screen content,
oversaturated colors, HDR halo, fisheye distortion, fast camera movement, shaky camera,
cartoon, illustration, 3d render look, stock photo smile
```

### Clipe 1 — A Logo que Vira Luz (~6s)

Quadro inicial: âncora do notebook. A tela é gerada **acesa em branco-azulado, sem conteúdo** —
a logo é camada HTML (seção 5).

```
Night interior, a simple cozy Brazilian living room, lights off. A laptop sits open on a
wooden table, its screen glowing soft blue-white, the only light source in the room. The glow
spills across the table surface and catches fine dust motes drifting in the air. The camera
pushes in very slowly toward the screen, the glow growing until it fills the frame with soft
blue light. Warm amber lamp far out of focus in the background. Shallow depth of field,
photoreal advertising cinematography, slow steady camera, volumetric light, natural shadows.
No people, no text, no readable screen content, no logos.
```

### Clipe 2 — A Máquina (~7s) — a única cena de tecnologia

`keyframes.start` = último quadro do clipe 1 (tela azul preenchendo o quadro).

```
From a wall of soft blue light, the camera dives into a network journey: a pulse of bright
blue light races along glowing fiber optic strands in darkness, then through a cinematic data
center aisle — tall server racks with blinking blue LEDs, cold air haze, the pulse of light
jumping from rack to rack down the corridor. The camera follows the pulse out and up: a
telecommunications tower at night emitting slow expanding rings of soft light over a Brazilian
city, thousands of small lights below. The last ring of light descends toward the warm-lit
doorway of a community center. Continuous forward camera movement, slow and steady. Photoreal
advertising cinematography, volumetric light, deep blue palette with warm amber accents.
No people, no text, no logos, no readable screens.
```

### Clipe 3 — A Caixa (~6s)

`keyframes.start` = imagem-herói da caixa (Nano Banana Pro, logo aplicada).

```
Inside a warm community center at dawn. Close on a beautiful premium kraft gift box on a
wooden table — clean craft cardboard, a blue ribbon tied in a neat bow. Volunteers' hands
close the lid with care, tuck the ribbon, and lift the box. Faces out of frame, hands only.
They carry it to a small white van outside; the box is placed among others like it. The van
door slides shut and it drives off into a city street at sunrise, cool blue night giving way
to golden morning light across the frame. Slow steady camera, shallow depth of field,
photoreal advertising cinematography, volumetric morning light, detailed materials.
No text, no readable labels, no logos except the small emblem on the box.
```

### Clipe 4 — A Chegada (~6s)

`keyframes.start` = último quadro do clipe 3 (van na rua dourada).

```
Golden morning light. The small white van stops in front of a modest Brazilian home. A
volunteer carries the beautiful kraft box with the blue ribbon to the doorway, where a child
of about seven and their guardian, a woman in her forties, receive it together. They kneel and
open it on the doorstep: the child lifts out a notebook and a small toy, eyes wide, genuine
laughter; the woman holds a folded blanket, moved, a quiet grateful smile. Warm sunlight
floods the doorway, long soft shadows. Slow steady camera at their level, shallow depth of
field, photoreal advertising cinematography, detailed realistic skin texture, natural
unposed joy. No text, no logos except the small emblem on the box.
```

### Clipe 5 — O Laço Fecha (~6s)

`keyframes.start` = último quadro do clipe 4. `keyframes.end` = imagem-herói da caixa em close
(a mesma do clipe 3), para o quadro final ser estável e idêntico à referência.

```
Same warm doorway. The camera slowly moves past the happy child and guardian, closing in on
the beautiful kraft box with the blue ribbon resting between them, until the box's small
emblem area fills the center of the frame in a stable, softly lit close-up. Fine particles of
warm light begin to lift gently from the box surface, drifting upward like slow golden dust.
The frame settles completely still. Shallow depth of field, photoreal advertising
cinematography, warm golden palette, volumetric light. No text, no readable content.
```

A re-formação da logo em tela **não é vídeo**: as partículas do quadro final entregam para a
camada HTML/GSAP, que reagrupa a logomarca real por cima do canvas (seção 5), ao lado da foto
da campanha e do CTA.

---

## 5. A logo — duas técnicas, dois contextos

**Na tela do notebook (clipes 1 e 5):** a logo **nunca é gerada por IA**. O vídeo traz a tela
acesa em branco-azulado; a logomarca real (`wwwroot/images/logo/logomarca.png`) entra como
**camada HTML/GSAP sobre o canvas**, posicionada sobre a área da tela — pixel-perfect. A
dissolução em partículas é dirigida pelo scroll e **reversível: rolar para trás re-forma a
logo**. No clipe 5 o mesmo mecanismo roda ao contrário: partículas convergem e a logo se
re-forma, entrando então no loop ambiente infinito.

**Na caixa (clipes 3–5):** a logo precisa existir dentro do vídeo. A imagem-herói da caixa é
gerada no **Nano Banana Pro** com `logomarca.png` como `references[].type: "image"` (é o
modelo mais forte em fidelidade de marca), iterando até a logo sair limpa. Essa imagem vira
`keyframes.start`/`end` dos clipes; câmera suave para a logo não deformar em movimento. No
quadro final estável, a camada HTML reforça a logo nítida por cima.

## Conteúdo HTML sobreposto

Tudo do repositório — nada inventado. Fontes: `Landing.razor`, `README.md`,
`docs/funcionalidades.md`.

| Clipe | Texto | Origem |
| --- | --- | --- |
| 1 | "Conectamos quem quer **ajudar** com quem **precisa**." + lead + CTAs "Quero Doar"/"Criar Campanha" | `Landing.razor:18-35` |
| 2 | Selos "100% Seguro · Transparência · Impacto Real" + faixa técnica: "sua doação não se perde" (Outbox), "processada exatamente uma vez" (idempotência), "acompanhe em tempo real" (SignalR) | `Landing.razor:39-50` + `README.md` → Padrões e decisões |
| 3 | "Como funciona" (3 passos) | `Landing.razor:314-322` |
| 4 | Números de impacto (`TransparenciaAsync()`) + "Sua instituição pode ampliar o impacto." | API + `Landing.razor:232-233` |
| 5 | "Tecnologia a serviço da solidariedade." + logo re-formada + **foto real da campanha** (`wwwroot/images/campanhas/web/*` / `CampaignCard` vivo da API) + CTA final | `Landing.razor:247-248` + API |

---

## 6. Produção no Magnific

### Modelos

| Papel | Modelo | Slug | Observação |
| --- | --- | --- | --- |
| Quadros-chave e caixa | Google Nano Banana Pro | `imagen-nano-banana-2` | Melhor fidelidade de marca (logo na caixa) |
| Clipes | **Seedance 1.5 Pro** | `bytedance-seedance-pro-1.5` | **Definido pelo usuário** (pediu "1.6 Pro"; a 1.x do catálogo é a 1.5) |
| Upgrade pontual | Seedance 2.0 Pro | `bytedance-seedance-pro-2.0` | Só se alguma cena crítica (rostos do clipe 4) não convencer |

Seedance 1.5 Pro: 1080p, 4–12s, `keyframes.start` **e** `end`, sem `references[]`, sem
`cameraMotion` estruturado, prompt ≤ 2.500 chars. Com clipe a 660 créditos, o blocking em
Kling deixou de valer a pena — itera-se direto no modelo final.

### Custos (simulados, exatos)

| Etapa | Cálculo | Créditos |
| --- | --- | --- |
| Âncora do notebook + variações | 2–4 × 75 (Nano Banana Pro 2K) | ~150–300 |
| Imagem-herói da caixa + variações | 2–4 × 75 + ref. da logomarca | ~150–300 |
| Quadros-chave start/end (~6 imagens) | 6 × 75 | ~450 |
| 5 clipes (~31s) | 110/s (1080p) | ~3.400 |
| Reserva de retakes (até 8 clipes) | 8 × 660 | ~5.300 |
| **Total** | | **~9.700 de ~44.570 (22%)** |

Gastos até agora: ~150 (duas âncoras da fase de exploração — reaproveitáveis como referência
de estilo). Modo ilimitado **inativo** nesta sessão: tudo consome créditos. Gates: âncoras →
aprovação → quadros-chave → aprovação → clipes.

---

## 7. Pipeline de quadros

Sequência em canvas (não `video.currentTime`, instável no Safari/iOS).

```bash
# ~31s de clipes na taxa NATIVA de 24 fps -> 744 quadros (acima de 24 o ffmpeg só
# duplica quadros). Numeração GLOBAL contínua (144/168/144/144/144 por clipe):
# -start_number = 1, 145, 313, 457, 601.
ffmpeg -i clipe-1.mp4 -vf "scale=1280:-2" -frames:v 144 -c:v libwebp -quality 50 -compression_level 6 -preset photo -start_number 1 desktop/frame-%04d.webp
ffmpeg -i clipe-1.mp4 -vf "scale=640:-2"  -frames:v 144 -c:v libwebp -quality 48 -compression_level 6 -preset photo -start_number 1 mobile/frame-%04d.webp
```

| Alvo | Largura | Por quadro | Total (744) |
| --- | --- | --- | --- |
| Desktop | 1280 px q50 | ~24 KB | **~17,3 MB** |
| Mobile | 640 px q48 | ~10 KB | **~7,0 MB** |

**Carregamento progressivo obrigatório:** o segmento do clipe 1 (144 quadros) bloqueia a
revelação da hero; o resto carrega em background. Só os blobs comprimidos ficam todos em
memória — a decodificação em ImageBitmap acontece numa janela de ±24 quadros ao redor do
quadro atual (`cinematic.js`), senão os 744 bitmaps somariam ~2,7 GB. Destino: `wwwroot/cinematic/{desktop,mobile}/`,
servido **sem fingerprint** (os quadros são carregados por JS, fora do `@Assets[...]`).
MP4 fonte em `assets/cinematic/source/` (fora de `wwwroot`).

---

## 8. Integração Blazor

O brief original pressupunha HTML estático; aqui há circuito SignalR, diff de DOM do servidor
e navegação enhanced.

- **Bibliotecas locais** em `wwwroot/lib/` (GSAP + ScrollTrigger + Lenis), padrão do Bootstrap
  já existente. O k8s roda `readOnlyRootFilesystem` + NetworkPolicy default-deny — sem CDN.
- **`cinematic.js`** módulo ES com `init(canvasRef)` / `destroy()`; interop via
  `IJSObjectReference` em `OnAfterRenderAsync(firstRender)` + `DisposeAsync`. O `destroy`
  mata todos os ScrollTriggers (`ScrollTrigger.getAll().forEach(t => t.kill())`), para o RAF
  do Lenis e libera as imagens. Sem isso, navegar `/` → `/campanhas` → `/` acumula triggers —
  o bug mais provável desta implementação.
- **Diff do Blazor × GSAP:** elementos animados por GSAP em marcação estática (sem
  `@if`/`@foreach` sobre eles); dados dinâmicos (contadores, cards) em containers separados.
- **Navegação enhanced:** listener `enhancedload` como rede de segurança (padrão em
  `site.js:128`).
- **Não tocar:** `NotificationConsumer`, `NotificationDispatcher`, `ReconnectModal`,
  `JwtAuthStateProvider`, `AppTheme.cs`, `AssistantChat`, demais rotas.

## 9. Responsivo, acessibilidade e fallback

| Condição | Comportamento |
| --- | --- |
| Desktop ≥ 1024px | Sequência completa 1440px, Lenis ativo |
| Tablet / mobile | Sequência 720px, menos quadros, Lenis com `duration` menor |
| `prefers-reduced-motion` | **Sem canvas/pin.** Imagem estática por seção — contrato já em `brand.css:94-103` e `474-478`; a logo aparece estática, sem dissolução |
| Conexão lenta / falha | Poster estático + conteúdo HTML normal; a landing nunca depende da mídia |
| Sem JS | Conteúdo e CTAs íntegros |

Texto sobre a mídia com véu `linear-gradient` para contraste AA — crítico nos clipes 4–5
(claros e dourados).

## 10. Plano de verificação

Com a stack no ar (`pwsh infra/k8s/up.ps1`, landing em `http://localhost:18088`):

- [ ] Scroll para baixo avança; **para cima retorna** sem salto — incluindo a logo se
      re-formando na hero ao voltar ao topo
- [ ] Nenhum quadro vazio/branco na timeline
- [ ] Dissolução da logo (clipe 1) e re-formação (clipe 5) alinhadas com a tela do notebook e
      com o close da caixa
- [ ] Loop ambiente da seção final roda infinito sem vazamento de memória
- [ ] Textos entram/saem nos momentos certos, sem sobreposição
- [ ] Console limpo — zero erros, zero 404
- [ ] `/` → `/campanhas` → `/`: recria sem duplicar ScrollTriggers
- [ ] Âncoras `#impacto`, `#como-funciona`, `#instituicoes`, `#sobre` funcionam com canvas pinado
- [ ] CTAs → `/campanhas` e `/cadastrar`
- [ ] Contraste AA sobre os clipes claros (4–5)
- [ ] Mobile real: fallback 720px, rolagem fluida
- [ ] `prefers-reduced-motion`: sem canvas, conteúdo íntegro
- [ ] Cadastro, login, doação e tempo real intactos
- [ ] Lighthouse: sem regressão grave de LCP/CLS

## 11. Riscos

| Risco | Mitigação |
| --- | --- |
| **Logo da caixa deformando em movimento** | Imagem-herói via Nano Banana Pro (brand fidelity) como keyframe start/end; câmera suave; camada HTML reforça no quadro final |
| Rostos deformados (clipe 4) | Planos médios, negative prompt específico; retakes a 660 créditos; upgrade pontual ao 2.0 Pro se preciso |
| Continuidade entre clipes | `keyframes.start`+`end` encadeados — controle mais firme que referências |
| ScrollTriggers acumulados em SPA | `destroy()` explícito + checklist |
| ~20 MB de quadros | Carregamento progressivo por segmento |
| GSAP × diff do Blazor | Separação estática/dinâmica (§8) |
| Estouro de créditos | Total ~22% do saldo; gates de aprovação por etapa |

## 12. Execução — status final

1. ✅ Backup: tag `pre-landing-cinematografica`, branch `feat/landing-cinematografica`
2. ✅ Este documento
3. ✅ Âncoras geradas e aprovadas (Notebook A + Caixa B)
4. ✅ Quadros-chave start/end (6 imagens, Porta A escolhida)
5. ✅ 5 clipes no Seedance 1.5 Pro 1080p (31s) → 310 quadros WebP desktop (11,7 MB) +
   310 mobile (4,1 MB); MP4 fonte em `assets/cinematic/source/`
6. ✅ Implementação: GSAP/ScrollTrigger/Lenis locais em `wwwroot/lib/`,
   `wwwroot/js/cinematic.js`, camada da logo, `Landing.razor` + CSS reescrito
7. ✅ Verificação em navegador real (Playwright/Chromium): **18/18 PASS** — scroll
   ida/volta com a logo dissolvendo/re-formando, canvas sem quadro vazio, console limpo,
   navegação SPA sem ScrollTriggers acumulados, âncoras do footer, reduced-motion
   estático, mobile com quadros 720px. Pendente apenas o smoke na stack k8s completa
   (dados vivos nos contadores/cards).

**Gasto real de créditos:** 4.690 de 45.000 (~10%) — âncoras 450 + quadros-chave 450 +
clipes 3.410 + exploração inicial 380. Bem abaixo dos ~9.700 orçados.

### Achados de implementação (vale conhecer)

- **Pin do ScrollTrigger quebrou com ancestral com `transform`** (layout MudBlazor):
  `position: fixed` do pin ficava relativo ao ancestral transformado e o stage saía da
  tela. Solução: **`position: sticky`** no CSS faz o pin; o ScrollTrigger só rastreia o
  progresso. Mais simples e imune ao problema.
- **Prerender bloqueava o primeiro paint em ~34s** com o Gateway fora (retries do
  pipeline de resiliência dentro de `OnInitializedAsync`). A carga de dados foi movida
  para `OnAfterRenderAsync` + `StateHasChanged`: primeiro paint em ~0,5s em qualquer
  cenário; a cena não depende da API.
- O `.exe` da Web fica bloqueado se recompilar com o serviço no ar (gotcha já documentado
  no skill `verify` do projeto).
