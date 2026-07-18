# Politica de Seguranca

## Principio 5 - Segredos nunca entram no Git

Nenhum segredo real pode ser versionado neste repositorio. Segredos ficam apenas em
variaveis de ambiente, arquivos `.env` locais (ignorados pelo Git), user-secrets do .NET
ou Kubernetes Secrets criados a partir de templates de exemplo.

### Proibido versionar

- Tokens JWT reais (assinados) de qualquer usuario.
- Senhas de banco de dados (PostgreSQL / `identitydb` / `campaignsdb` / `zabbixdb`).
- Credenciais do RabbitMQ (usuario e senha).
- Credenciais do Grafana (usuario e senha admin).
- Credenciais e senhas do Zabbix.
- Arquivos `.env` reais (apenas `.env.example` com placeholders pode ser versionado).
- Connection strings de producao (ou qualquer connection string com senha inline).
- Segredos JWT (`Jwt__Secret` / `JWT_SECRET`).
- `infra/k8s/secret.yaml` preenchido (apenas `secret.example.yaml` pode ser versionado).
- **Personal Access Token do Docker Hub (`DOCKERHUB_TOKEN`)** usado para publicar as imagens.
- Chave da OpenAI (`OPENAI_API_KEY`), opcional para as features de IA do Web.

### Como configurar os segredos

- Docker Compose: copie `.env.example` para `.env` e preencha os valores. O Docker Compose
  le o `.env` automaticamente.
- Kubernetes: copie `infra/k8s/secret.example.yaml` para `infra/k8s/secret.yaml`, preencha
  e aplique com `kubectl apply -f infra/k8s/secret.yaml`. (O `infra/k8s/up.ps1` gera esse
  Secret automaticamente a partir do `.env` da raiz.)
- Desenvolvimento local (.NET): use user-secrets (`dotnet user-secrets set`) ou variaveis
  de ambiente.

### Publicacao de imagens no Docker Hub

O `infra/k8s/push-dockerhub.ps1` publica as 5 imagens da aplicacao. O token **nunca** e
gravado em arquivo nem passado por linha de comando (que fica no historico do shell): ele e
lido da variavel de ambiente `DOCKERHUB_TOKEN` e enviado ao Docker via `--password-stdin`.

```powershell
$env:DOCKERHUB_TOKEN = "<PAT>"     # apenas na sessao; nunca commitar
pwsh infra/k8s/push-dockerhub.ps1
```

Apos o primeiro login, a credencial fica no credential store do Docker e o token nao e mais
necessario (use `-SkipLogin`). Recomendacoes: gerar um PAT com escopo **somente de escrita nos
repositorios necessarios**, nunca reutilizar a senha da conta, e **rotacionar o PAT** se ele
for exposto (chat, log, print de tela ou historico de comandos). Os repositorios
`junonn5/conexao-solidaria-*` sao **publicos** — publique apenas artefatos sem dado sensivel
(as imagens nao devem conter `.env`, segredos ou connection strings embutidas).

## Como reportar uma vulnerabilidade

Reporte vulnerabilidades de seguranca de forma privada para f.junior.gy@gmail.com. Nao abra
issues publicas com detalhes de vulnerabilidades ou com segredos. Inclua passos de
reproducao e o impacto potencial. Respostas sao enviadas assim que possivel.

## Segredos ja commitados devem ser rotacionados

Qualquer segredo que tenha sido commitado no repositorio (mesmo que posteriormente removido)
deve ser considerado comprometido e ROTACIONADO imediatamente: gere novas senhas, novos
segredos JWT e novas credenciais, e invalide os valores antigos. Remover o segredo do
codigo nao basta, pois ele permanece no historico do Git.
