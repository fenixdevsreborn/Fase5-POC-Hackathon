# Roteiro do video de demonstracao

Tempo maximo exigido no desafio: 15 minutos.

Objetivo do video: comprovar a arquitetura, o funcionamento da solucao e os requisitos obrigatorios do hackathon, sem ler codigo linha a linha.

## Preparacao antes de gravar

Deixe estes itens prontos antes de iniciar a gravacao:

1. Repositorio aberto no GitHub, com um workflow de CI ja executado com sucesso.
2. Docker Desktop aberto e Kubernetes habilitado.
3. Contexto Kubernetes selecionado:

```powershell
kubectl config use-context docker-desktop
```

4. Imagens locais criadas:

```powershell
docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile -t conexao-solidaria/identity-api:local .
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile -t conexao-solidaria/campaigns-api:local .
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile -t conexao-solidaria/donations-worker:local .
```

5. Aplicacao subida:

```powershell
kubectl apply -f infra/k8s/conexao-solidaria.yaml
kubectl wait --for=condition=Ready pod --all -n conexao-solidaria --timeout=300s
```

6. Abas sugeridas abertas:
   - GitHub Actions do repositorio.
   - `docs/arquitetura.md`.
   - Terminal.
   - Identity Swagger: http://localhost:30081/swagger
   - Campaigns Swagger: http://localhost:30082/swagger
   - RabbitMQ: http://localhost:31672
   - Elasticsearch: http://localhost:30200
   - Grafana: http://localhost:30300
   - Prometheus: http://localhost:30090
   - Zabbix: http://localhost:30085

Credenciais uteis:

| Recurso | Usuario | Senha |
| --- | --- | --- |
| Gestor ONG | `gestor@conexaosolidaria.local` | `Gestor@123456` |
| RabbitMQ | `guest` | `guest` |
| Grafana | `admin` | `admin` |
| Zabbix | `Admin` | `zabbix` |

## Marcacao de tempo sugerida

| Tempo | Bloco |
| --- | --- |
| 0:00 - 0:45 | Abertura e contexto do problema |
| 0:45 - 2:30 | Arquitetura |
| 2:30 - 3:45 | Pipeline CI/CD |
| 3:45 - 5:15 | Kubernetes e pods rodando |
| 5:15 - 6:45 | Observabilidade |
| 6:45 - 12:30 | Fluxo funcional completo |
| 12:30 - 13:30 | Regra de doacao rejeitada |
| 13:30 - 14:20 | Requisitos opcionais |
| 14:20 - 15:00 | Encerramento |

## 1. Abertura

Mostrar: tela do repositorio ou README.

Fala sugerida:

> Ola, somos o grupo [NOME_DO_GRUPO]. Neste video vamos demonstrar o MVP da plataforma Conexao Solidaria, criada para a ONG Esperanca Solidaria. O objetivo do projeto e substituir a gestao manual de doadores e campanhas por uma solucao digital escalavel, observavel e automatizada.

> A solucao foi desenvolvida em .NET 10, com APIs REST documentadas via Swagger, autenticacao JWT com RBAC, PostgreSQL como banco principal, Elasticsearch para busca fuzzy no painel de transparencia, RabbitMQ para mensageria assincrona, Kubernetes no Docker Desktop, observabilidade com Prometheus, Grafana e Zabbix, alem de pipeline de CI no GitHub Actions.

Observacao:

- Fale em ritmo direto. Este bloco deve ser curto.
- Nao entre em codigo ainda.

## 2. Arquitetura

Mostrar: `docs/arquitetura.md`.

Fala sugerida:

> Aqui esta o desenho da arquitetura. A solucao foi separada em tres componentes principais. A Identity API cuida do cadastro de doadores, login e emissao de tokens JWT. A Campaigns API cuida da gestao das campanhas, do painel publico de transparencia e da criacao da intencao de doacao. O Donations Worker e o consumidor assincrono responsavel por processar as mensagens de doacao e atualizar o valor arrecadado.

> Usamos PostgreSQL em dois contextos principais: `identitydb` para usuarios e autenticacao, e `campaignsdb` para campanhas e doacoes. A Campaigns API tambem indexa os titulos das campanhas no Elasticsearch para permitir busca fuzzy, tolerante a erro de digitacao, em um endpoint separado de busca da transparencia. O RabbitMQ fica entre a API de campanhas e o worker, garantindo que a API nao atualize diretamente o valor arrecadado quando uma doacao chega.

> A observabilidade e feita expondo `/health` e `/metrics` nas aplicacoes. O Prometheus coleta essas metricas, o Grafana exibe os dashboards e o Zabbix pode monitorar os endpoints de saude.

Apontar no diagrama:

- `Identity.Api (.NET 10)`.
- `Campaigns.Api (.NET 10)`.
- `Donations.Worker (.NET 10)`.
- `RabbitMQ`.
- `Elasticsearch`.
- `PostgreSQL`.
- `Prometheus`, `Grafana` e `Zabbix`.

Requisitos comprovados neste bloco:

- Arquitetura de microsservicos.
- Banco de dados PostgreSQL.
- Comunicacao assincrona via broker.
- Observabilidade.

## 3. Pipeline CI/CD

Mostrar: GitHub Actions do repositorio e arquivo `.github/workflows/ci.yml`.

Fala sugerida:

> O desafio exige uma esteira acionada a cada push na branch principal. Aqui temos o workflow de CI configurado no GitHub Actions. Ele executa restore, build, testes e tambem gera as imagens Docker das tres aplicacoes: Identity API, Campaigns API e Donations Worker.

Mostrar no arquivo:

```text
dotnet restore ConexaoSolidaria.slnx
dotnet build ConexaoSolidaria.slnx --configuration Release --no-restore
dotnet test ConexaoSolidaria.slnx --configuration Release --no-build --logger trx
docker build -f src/ConexaoSolidaria.Identity.Api/Dockerfile
docker build -f src/ConexaoSolidaria.Campaigns.Api/Dockerfile
docker build -f src/ConexaoSolidaria.Donations.Worker/Dockerfile
```

Fala sugerida:

> Aqui no historico do GitHub Actions podemos ver a execucao concluida com sucesso, comprovando que o projeto compila, executa os testes e gera as imagens Docker automaticamente.

Requisitos comprovados neste bloco:

- Pipeline automatizado.
- Build .NET.
- Docker build no CI.
- Testes no CI como requisito opcional implementado.

## 4. Kubernetes

Mostrar: terminal.

Executar:

```powershell
kubectl config current-context
kubectl get pods -n conexao-solidaria
kubectl get svc -n conexao-solidaria
```

Fala sugerida:

> Agora vamos demonstrar a aplicacao rodando no Kubernetes local do Docker Desktop. O contexto atual e `docker-desktop`, e todos os recursos foram criados no namespace `conexao-solidaria`.

> No comando `kubectl get pods`, vemos os pods da Identity API, Campaigns API, Donations Worker, PostgreSQL, RabbitMQ, Elasticsearch, Prometheus, Grafana e Zabbix. Isso comprova que a solucao esta orquestrada no Kubernetes.

> No comando `kubectl get svc`, vemos os services e os NodePorts usados para acesso local aos recursos.

URLs para mencionar:

- Identity Swagger: http://localhost:30081/swagger
- Campaigns Swagger: http://localhost:30082/swagger
- RabbitMQ: http://localhost:31672
- Elasticsearch: http://localhost:30200
- Prometheus: http://localhost:30090
- Grafana: http://localhost:30300
- Zabbix: http://localhost:30085

Requisitos comprovados neste bloco:

- Deployments e Services no Kubernetes.
- Aplicacao rodando no cluster local.
- Infraestrutura de apoio rodando no mesmo ambiente.

## 5. Observabilidade

Mostrar: Prometheus, Grafana e Zabbix.

### 5.1. Prometheus

Abrir: http://localhost:30090

Executar query:

```promql
up
```

Fala sugerida:

> No Prometheus, conseguimos validar que os targets da aplicacao estao ativos. A query `up` mostra se os servicos monitorados estao respondendo. Tambem temos scraping das metricas expostas pelas aplicacoes em `/metrics`.

Mostrar query:

```promql
sum(rate(http_requests_received_total[1m])) by (job)
```

Fala sugerida:

> Essa consulta mostra a taxa de requisicoes HTTP por servico, que sera refletida no Grafana.

### 5.2. Grafana

Abrir: http://localhost:30300

Login:

```text
admin / admin
```

Abrir dashboard:

```text
Conexao Solidaria - Kubernetes
```

Fala sugerida:

> No Grafana, temos um dashboard configurado para exibir metricas reais da aplicacao em execucao. Aqui vemos requisicoes HTTP por segundo, doacoes processadas e tentativas rejeitadas por campanha encerrada ou cancelada.

Observacao:

- Se o dashboard estiver sem dados, gere trafego nos Swagger e aguarde o refresh de 5 segundos.

### 5.3. Zabbix

Abrir: http://localhost:30085

Login:

```text
Admin / zabbix
```

Fala sugerida:

> O Zabbix tambem esta disponivel no cluster para monitoramento de disponibilidade. Ele pode ser configurado com HTTP checks para os endpoints `/health` da Identity API, Campaigns API e Donations Worker.

Se os itens ja estiverem configurados no Zabbix, mostrar:

- Host `Conexao Solidaria`.
- Items HTTP agent.
- Web scenario de health checks.

Fala opcional:

> Aqui temos o host Conexao Solidaria com checks HTTP apontando para os endpoints de saude dos servicos. Isso permite acompanhar disponibilidade e tempo de resposta.

Requisitos comprovados neste bloco:

- `/health`.
- `/metrics`.
- Grafana com dashboard real.
- Zabbix disponivel/configuravel para monitoramento.

## 6. Demonstracao funcional completa

Este bloco e o mais importante. A demonstracao deve mostrar autenticacao, criacao de campanha, doacao, RabbitMQ e atualizacao pelo worker.

### 6.1. Login do gestor

Mostrar: Identity Swagger em http://localhost:30081/swagger

Endpoint:

```text
POST /api/auth/login
```

Payload:

```json
{
  "email": "gestor@conexaosolidaria.local",
  "senha": "Gestor@123456"
}
```

Fala sugerida:

> Primeiro vamos autenticar como GestorONG. Esse perfil tem permissao para criar e editar campanhas. A API retorna um JWT, que sera usado na Campaigns API para acessar os endpoints protegidos.

Acao:

- Copiar `accessToken`.

Requisito comprovado:

- Autenticacao JWT.
- Role `GestorONG`.

### 6.2. Criacao de campanha

Mostrar: Campaigns Swagger em http://localhost:30082/swagger

Acao:

- Clicar em `Authorize`.
- Informar `Bearer TOKEN_DO_GESTOR`.

Endpoint:

```text
POST /api/campanhas
```

Payload:

```json
{
  "titulo": "Natal Solidario",
  "descricao": "Campanha para arrecadacao de brinquedos e alimentos.",
  "dataInicio": "2026-06-01T00:00:00Z",
  "dataFim": "2026-12-31T23:59:59Z",
  "metaFinanceira": 10000,
  "status": "Ativa"
}
```

Fala sugerida:

> Agora criamos uma campanha como GestorONG. O cadastro exige titulo, descricao, data de inicio, data de fim, meta financeira e status. As regras de negocio impedem meta menor ou igual a zero e data de fim no passado.

Acao:

- Copiar o `id` da campanha criada.

Requisitos comprovados:

- Gestao de campanhas protegida por role.
- Regras de negocio da campanha.
- Swagger nas APIs.

### 6.3. Cadastro de doador

Mostrar: Identity Swagger.

Endpoint:

```text
POST /api/auth/cadastro-doador
```

Payload:

Use um email novo a cada gravacao. Exemplo:

```json
{
  "nomeCompleto": "Maria Doadora",
  "email": "maria.doadora.video@example.com",
  "cpf": "390.533.447-05",
  "senha": "Doador@123"
}
```

Fala sugerida:

> Agora cadastramos um doador por um endpoint publico. O sistema valida CPF, exige email unico e salva a senha com hash usando BCrypt. O retorno tambem traz um JWT, agora com a role Doador.

Acao:

- Copiar `accessToken` do doador.

Requisitos comprovados:

- Cadastro publico de doador.
- CPF validado.
- Email unico.
- Senha armazenada com hash.
- Role `Doador`.

Observacao:

- Se o email ou CPF ja existir, troque o email e use outro CPF valido de teste.

### 6.4. Preparar RabbitMQ para visualizar a mensagem

Como o worker consome muito rapido, pause o worker antes de enviar a doacao.

Mostrar: terminal.

Executar:

```powershell
kubectl scale deployment/donations-worker --replicas=0 -n conexao-solidaria
kubectl get pods -n conexao-solidaria
```

Fala sugerida:

> Para deixar a passagem da mensagem visivel no RabbitMQ, vamos pausar temporariamente o worker. Assim, quando a API publicar o evento de doacao, a mensagem ficara na fila ate religarmos o consumer.

Mostrar: RabbitMQ em http://localhost:31672

Acao:

- Login `guest` / `guest`.
- Abrir `Queues and Streams`.
- Selecionar fila `doacoes-recebidas`.

### 6.5. Enviar intencao de doacao

Mostrar: Campaigns Swagger.

Acao:

- Clicar em `Authorize`.
- Informar `Bearer TOKEN_DO_DOADOR`.

Endpoint:

```text
POST /api/doacoes
```

Payload:

```json
{
  "idCampanha": "ID_DA_CAMPANHA_ATIVA",
  "valorDoacao": 150
}
```

Fala sugerida:

> Agora o doador envia uma intencao de doacao para a campanha ativa. Repare que a API retorna status de aceite, mas ela nao atualiza diretamente o total arrecadado. Ela grava a doacao como pendente e publica um evento `DoacaoRecebidaEvent` no RabbitMQ.

Mostrar: RabbitMQ.

Fala sugerida:

> No RabbitMQ, conseguimos ver a mensagem na fila `doacoes-recebidas`. Essa mensagem representa o evento assincrono que sera consumido pelo worker.

Requisitos comprovados:

- Processo de doacao por doador logado.
- Comunicacao assincrona.
- Evento publicado em broker.
- API nao atualiza diretamente o total arrecadado.

### 6.6. Religando worker e comprovando atualizacao

Mostrar: terminal.

Executar:

```powershell
kubectl scale deployment/donations-worker --replicas=1 -n conexao-solidaria
kubectl rollout status deployment/donations-worker -n conexao-solidaria
kubectl logs -f deployment/donations-worker -n conexao-solidaria
```

Fala sugerida:

> Agora religamos o worker. Ele consome a mensagem, valida a campanha, marca a doacao como processada e atualiza o valor total arrecadado no banco de campanhas.

Mostrar: Campaigns Swagger.

Endpoint:

```text
GET /api/campanhas/transparencia?page=1&pageSize=10&titulo=Natal
```

Fala sugerida:

> Por fim, consultamos o endpoint publico de transparencia com paginacao e filtros dinamicos. Ele retorna somente campanhas ativas e permite filtrar por titulo, faixa de meta financeira, faixa de valor arrecadado e periodo de data final. A resposta tambem traz os metadados de pagina, tamanho da pagina, total de itens e total de paginas. O valor atualizado confirma que o processamento foi feito pelo worker, apos o consumo da mensagem.

Mostrar tambem a busca com erro de digitacao:

```text
GET /api/campanhas/transparencia-search?page=1&pageSize=10&titulo=Ntal
```

Fala sugerida:

> Aqui demonstramos um requisito adicional de busca em um endpoint separado: mesmo digitando `Ntal`, sem a letra `a`, o endpoint encontra a campanha `Natal Solidario`. Isso acontece porque o campo titulo e consultado no Elasticsearch com fuzzy search; depois a API usa os IDs retornados para aplicar as regras finais no PostgreSQL. Esse endpoint tem paginacao, mas nao expõe os filtros dinamicos de meta, valor ou data.

Requisitos comprovados:

- Worker consumidor.
- Atualizacao assincrona do valor arrecadado.
- Painel publico de transparencia.
- Listagem apenas de campanhas ativas.
- Busca fuzzy por titulo com Elasticsearch.

## 7. Demonstrar regra de doacao rejeitada

Objetivo: provar que a doacao nao pode ser feita para campanha cancelada ou encerrada.

### 7.1. Cancelar campanha

Mostrar: Campaigns Swagger autorizado com token do gestor.

Endpoint:

```text
PUT /api/campanhas/{id}
```

Payload:

```json
{
  "titulo": "Natal Solidario",
  "descricao": "Campanha para arrecadacao de brinquedos e alimentos.",
  "dataInicio": "2026-06-01T00:00:00Z",
  "dataFim": "2026-12-31T23:59:59Z",
  "metaFinanceira": 10000,
  "status": "Cancelada"
}
```

Fala sugerida:

> Agora vamos alterar a campanha para o status Cancelada, para comprovar a regra de negocio que impede doacoes em campanhas encerradas ou canceladas.

### 7.2. Tentar doar novamente

Mostrar: Campaigns Swagger autorizado com token do doador.

Endpoint:

```text
POST /api/doacoes
```

Payload:

```json
{
  "idCampanha": "ID_DA_CAMPANHA_CANCELADA",
  "valorDoacao": 50
}
```

Fala sugerida:

> Ao tentar doar para uma campanha cancelada, a API retorna erro informando que a doacao nao e permitida para campanhas encerradas ou canceladas. Essa tentativa tambem alimenta a metrica de rejeicoes por campanha, exibida no Grafana.

Mostrar: Grafana.

Fala sugerida:

> No Grafana, o painel de rejeitadas por campanha mostra esse tipo de tentativa rejeitada. Ele nao contabiliza erro de payload, como valor invalido; ele representa a regra de negocio de campanha cancelada ou encerrada.

Requisitos comprovados:

- Regra de negocio de doacao.
- Observabilidade sobre rejeicoes de negocio.

## 8. Requisitos opcionais

Mostrar: terminal e workflow.

Executar:

```powershell
dotnet test ConexaoSolidaria.slnx
```

Fala sugerida:

> Como requisito opcional, implementamos testes de unidade com xUnit para regras de dominio, incluindo validacao de CPF e regras de campanha. Esses testes tambem rodam na esteira de CI, como mostramos no GitHub Actions.

Mostrar:

- `tests/ConexaoSolidaria.Tests/CpfValidatorTests.cs`
- `tests/ConexaoSolidaria.Tests/CampaignRuleTests.cs`
- `.github/workflows/ci.yml`

Fala sugerida:

> O outro item opcional era o uso de API Gateway. Nesta versao do MVP, optamos por nao adicionar um gateway para manter a entrega mais direta e focada nos requisitos obrigatorios. As APIs estao acessiveis separadamente via seus services no Kubernetes.

Requisitos opcionais:

- Testes de unidade: implementado.
- API Gateway: nao implementado nesta versao.

## 9. Encerramento

Mostrar: README ou tela do Grafana com dashboard.

Fala sugerida:

> Com isso, demonstramos os principais requisitos do desafio: autenticacao JWT com RBAC, cadastro de doador, gestao de campanhas, painel publico de transparencia, processamento assincrono de doacoes com RabbitMQ e worker, deploy em Kubernetes, observabilidade com Prometheus, Grafana e Zabbix, alem de pipeline de CI/CD com build, testes e geracao das imagens Docker.

> A documentacao do repositorio inclui os passos para subir a aplicacao localmente, o guia de Kubernetes, o guia de observabilidade, o diagrama de arquitetura e a justificativa da escolha dos bancos.

> Obrigado.

## Checklist dos requisitos no video

Use este checklist antes de finalizar a gravacao:

| Requisito | Onde mostrar |
| --- | --- |
| JWT | Login no Swagger da Identity API |
| Roles `GestorONG` e `Doador` | Token do gestor e token do doador |
| Endpoints de gestao protegidos | Criacao/edicao de campanha com token do gestor |
| Campanha com campos obrigatorios | Payload de criacao da campanha |
| Regra de DataFim e MetaFinanceira | Fala durante criacao de campanha |
| Cadastro publico de doador | `POST /api/auth/cadastro-doador` |
| Email unico | Fala no cadastro de doador |
| CPF validado | Fala no cadastro de doador |
| Senha com hash | Fala no cadastro de doador |
| Painel publico de transparencia | `GET /api/campanhas/transparencia?page=1&pageSize=10&titulo=Natal` |
| Busca fuzzy por titulo | `GET /api/campanhas/transparencia-search?page=1&pageSize=10&titulo=Ntal` |
| Doacao por doador logado | `POST /api/doacoes` com token de doador |
| Bloqueio para campanha cancelada/encerrada | Tentar doar apos cancelar campanha |
| Microsservicos | Diagrama e pods |
| Mensageria assincrona | RabbitMQ com fila `doacoes-recebidas` |
| Worker consumidor | Logs e atualizacao do valor arrecadado |
| Kubernetes | `kubectl get pods` e `kubectl get svc` |
| Health/metrics | Prometheus e endpoints `/health`, `/metrics` |
| Grafana | Dashboard com metricas reais |
| Zabbix | Tela web e checks de saude, se configurados |
| CI/CD | GitHub Actions |
| Docker build no CI | Steps de Docker build |
| Testes opcionais | `dotnet test` e workflow |
| API Gateway opcional | Informar que nao foi implementado |

## Comandos uteis para a gravacao

```powershell
kubectl config use-context docker-desktop
kubectl get pods -n conexao-solidaria
kubectl get svc -n conexao-solidaria
kubectl logs -f deployment/donations-worker -n conexao-solidaria
kubectl scale deployment/donations-worker --replicas=0 -n conexao-solidaria
kubectl scale deployment/donations-worker --replicas=1 -n conexao-solidaria
kubectl rollout status deployment/donations-worker -n conexao-solidaria
dotnet test ConexaoSolidaria.slnx
```

## Plano B se o RabbitMQ consumir rapido demais

Se a mensagem desaparecer antes de mostrar na tela:

1. Pause o worker:

```powershell
kubectl scale deployment/donations-worker --replicas=0 -n conexao-solidaria
```

2. Envie a doacao.
3. Mostre a fila no RabbitMQ.
4. Religue o worker:

```powershell
kubectl scale deployment/donations-worker --replicas=1 -n conexao-solidaria
```

5. Mostre o valor atualizado no endpoint de transparencia.

## Plano B se o Zabbix nao estiver configurado

Se nao houver tempo para configurar os items no Zabbix durante a gravacao:

Fala sugerida:

> O Zabbix esta provisionado e disponivel no cluster. A configuracao dos checks HTTP esta documentada no `ReadmeObservabilidade.md`, onde usamos HTTP agent ou Web scenarios para monitorar os endpoints `/health` das aplicacoes.

Mostrar:

- Zabbix Web aberto.
- `ReadmeObservabilidade.md` na secao de Zabbix.

## Plano B se o GitHub Actions nao tiver run recente

Antes da gravacao, faca um commit simples de documentacao e envie para a branch principal:

```powershell
git add .
git commit -m "docs: add video roteiro"
git push
```

Depois abra o GitHub Actions e aguarde o workflow concluir.
