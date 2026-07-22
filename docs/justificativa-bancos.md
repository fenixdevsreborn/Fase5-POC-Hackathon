# Justificativa dos bancos de dados

A solucao usa persistencia poliglota: cada tecnologia de armazenamento entrou por um motivo
especifico, e nao por variedade. O resumo e:

| Armazenamento | Papel | Onde |
| --- | --- | --- |
| PostgreSQL | Fonte da verdade transacional | `identitydb`, `campaignsdb`, `zabbixdb` |
| Elasticsearch | Indice derivado para busca de campanhas | indice `campanhas` |
| RabbitMQ | Broker de mensagens (nao e banco) | exchange `conexao-solidaria` |

## PostgreSQL para Identity

O `identitydb` guarda usuarios, roles, emails unicos, CPF e hash de senha. PostgreSQL foi escolhido
porque oferece consistencia transacional, indices unicos confiaveis e boa compatibilidade com
Entity Framework Core no .NET 10.

Emails e CPF sao dados que exigem unicidade real: a garantia precisa vir de constraint no banco, e
nao de uma verificacao na aplicacao, que perde a corrida quando duas requisicoes chegam juntas.

## PostgreSQL para Campanhas e Doacoes

O `campaignsdb` guarda campanhas, doacoes pendentes/processadas e o total arrecadado
(`CampaignStats`). O modelo e relacional: uma doacao pertence a uma campanha, as regras exigem
consultas consistentes e o valor total arrecadado precisa ser atualizado de forma segura pelo
worker.

Alem do modelo relacional, tres mecanismos do fluxo assincrono dependem de transacao ACID e de
constraints unicas — e sao eles que tornam o PostgreSQL a escolha certa aqui, e nao um banco
nao-relacional:

- **Outbox** (`OutboxMessages`): a doacao e a mensagem a publicar sao gravadas na *mesma*
  transacao. Sem isso, uma queda entre o commit e o publish perderia o evento.
- **Idempotencia de entrada** (`DonationIdempotencyKeys`): a chave de idempotencia enviada pelo
  cliente e unica no banco, entao um retry da mesma doacao nao gera cobranca duplicada.
- **Idempotencia de consumo** (`ProcessedMessages`): o worker registra o id da mensagem ja
  processada, o que torna seguro o `at-least-once` do broker.

## RabbitMQ como broker, nao como banco

RabbitMQ foi escolhido para desacoplar a intencao de doacao do processamento, demonstrando a
comunicacao assincrona exigida pelo desafio. Ele transporta mensagens; nada de estado de negocio
vive nele.

O fluxo real nao publica direto do endpoint. Ele e:

1. `DoacoesController` grava a doacao **e** a `OutboxMessage` com o `DoacaoRecebidaEvent` numa unica
   transacao no PostgreSQL, e responde ao cliente.
2. `OutboxDispatcherWorker` varre as mensagens pendentes, publica no broker com *publisher
   confirms* e marca como publicada. Em falha, aplica backoff incremental e tenta de novo.
3. `DonationConsumerWorker` consome a fila `doacoes-recebidas`, descarta o que ja processou
   (`ProcessedMessages`) e atualiza o total arrecadado no PostgreSQL.

Essa separacao e o que garante entrega ao menos uma vez sem duplicar efeito: o broker pode
reentregar a vontade, porque a deduplicacao esta no banco relacional.

## Elasticsearch para a busca de campanhas

A listagem simples de campanhas e servida pelo PostgreSQL. Quando ha **termo de busca**, a consulta
vai para o Elasticsearch (indice `campanhas`), porque busca textual com relevancia, analise de
tokens e filtros combinados e exatamente o que um indice invertido resolve bem e um `LIKE` em SQL
resolve mal — sem indice de texto o custo cresce com a tabela inteira e o resultado nao tem ranking.

Decisoes que valem registrar:

- **O PostgreSQL continua sendo a fonte da verdade.** O Elasticsearch e um indice *derivado*:
  perder o indice significa reindexar, nunca perder dado.
- **Indexacao best-effort.** Se a indexacao de uma campanha falhar, a operacao de escrita segue
  normalmente e apenas registra log — uma indisponibilidade do ES nao pode derrubar a criacao de
  campanha.
- **Degradacao graciosa na leitura.** A busca e protegida por um circuit breaker
  (`elasticsearch-search`). Com o circuito aberto ou em timeout, a busca cai para o PostgreSQL
  aplicando o *mesmo* recorte de filtro (`CampaignSearchFilter`), para que a queda do ES nao
  exponha campanhas que o usuario nao deveria ver. O resultado perde qualidade de ranking, mas o
  sistema nao perde a funcionalidade.
- **Criacao em lote indexa uma vez so.** A gravacao e separada da indexacao para que um lote pague
  um unico `IndexManyAsync`, em vez de N chamadas ao ES.

## Zabbix

O `zabbixdb` tambem usa PostgreSQL, na mesma instancia dos demais bancos, para simplificar a
infraestrutura local e manter uma unica tecnologia de banco relacional no ambiente. O Zabbix roda
com usuario proprio (`zabbix`) e banco proprio, isolado dos bancos da aplicacao.

## Por que nao um banco unico

Concentrar tudo no PostgreSQL custaria a qualidade da busca textual; concentrar tudo no
Elasticsearch custaria transacao, constraint unica e o padrao Outbox — que sao a base das garantias
do fluxo de doacao. Cada um faz o que faz bem, com o relacional no papel de fonte da verdade e o
indice no papel de acelerador substituivel.
