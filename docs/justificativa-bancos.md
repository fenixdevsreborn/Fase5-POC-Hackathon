# Justificativa dos bancos de dados

## PostgreSQL para Identity

O `identitydb` guarda usuarios, roles, emails unicos, CPF e hash de senha. PostgreSQL foi escolhido porque oferece consistencia transacional, indices unicos confiaveis e boa compatibilidade com Entity Framework Core no .NET 10.

## PostgreSQL para Campanhas e Doacoes

O `campaignsdb` guarda campanhas, doacoes pendentes/processadas e o total arrecadado. O modelo e relacional: uma doacao pertence a uma campanha, as regras exigem consultas consistentes e o valor total arrecadado precisa ser atualizado de forma segura pelo worker.

## RabbitMQ como broker, nao como banco

RabbitMQ foi escolhido para desacoplar a intencao de doacao do processamento. A API publica o evento `DoacaoRecebidaEvent`; o worker consome a mensagem e atualiza o PostgreSQL. Isso evita acoplamento direto e demonstra comunicacao assincrona, que e um requisito do desafio.

## Zabbix

O `zabbixdb` tambem usa PostgreSQL para simplificar a infraestrutura local e manter uma unica tecnologia de banco relacional no ambiente.
