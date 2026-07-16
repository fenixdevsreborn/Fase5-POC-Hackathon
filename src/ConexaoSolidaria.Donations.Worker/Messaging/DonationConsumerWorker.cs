using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConexaoSolidaria.Shared.Domain;
using ConexaoSolidaria.Contracts.Events;
using ConexaoSolidaria.Contracts.Messaging;
using ConexaoSolidaria.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ConexaoSolidaria.Donations.Worker.Messaging;

public sealed class DonationConsumerWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    IConfiguration configuration,
    ILogger<DonationConsumerWorker> logger) : BackgroundService
{
    private const string AttemptsHeader = "x-attempts";
    private const string TraceParentHeader = "traceparent";

    private static readonly ActivitySource ActivitySource = new("ConexaoSolidaria.Donations.Worker");

    private static readonly Counter ProcessedDonations = Metrics.CreateCounter(
        "conexao_donations_processed_total",
        "Quantidade de doacoes processadas pelo worker.");

    private static readonly Counter RejectedDonations = Metrics.CreateCounter(
        "conexao_donations_rejected_total",
        "Quantidade de tentativas de doacao rejeitadas por campanha encerrada ou cancelada.");

    // Valor monetario total arrecadado em doacoes processadas (BRL). KPI de negocio central:
    // complementa a CONTAGEM de doacoes com o VALOR efetivamente arrecadado.
    private static readonly Counter ProcessedAmount = Metrics.CreateCounter(
        "conexao_donation_amount_brl_total",
        "Valor total arrecadado em doacoes processadas, em BRL.");

    // Metricas por campanha (label 'campanha' = titulo). Cardinalidade limitada pelo numero de
    // campanhas ativas; adequado para o cenario da POC. Permite ranking (top campanhas) e valor
    // arrecadado por campanha nos dashboards.
    private static readonly Counter ProcessedByCampaign = Metrics.CreateCounter(
        "conexao_donations_by_campaign_total",
        "Quantidade de doacoes processadas por campanha.",
        new CounterConfiguration { LabelNames = new[] { "campanha" } });

    private static readonly Counter ProcessedAmountByCampaign = Metrics.CreateCounter(
        "conexao_donation_amount_by_campaign_brl_total",
        "Valor arrecadado por campanha, em BRL.",
        new CounterConfiguration { LabelNames = new[] { "campanha" } });

    private static readonly Counter DeadLetterMessages = Metrics.CreateCounter(
        "conexao_dead_letter_messages",
        "Quantidade de mensagens enviadas para a dead-letter queue.");

    private static readonly Histogram ProcessingDuration = Metrics.CreateHistogram(
        "conexao_donation_processing_duration_seconds",
        "Duracao do processamento de cada doacao pelo worker, em segundos.");

    private readonly RabbitMqOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConsumerAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no consumo do RabbitMQ. Nova tentativa em 5 segundos.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunConsumerAsync(CancellationToken stoppingToken)
    {
        var factory = RabbitMqConnectionFactoryBuilder.Build(configuration, _options);

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await DeclareTopologyAsync(channel, stoppingToken);

        // Prefetch moderado (10): o broker entrega ate 10 mensagens nao confirmadas por vez, reduzindo
        // round-trips e aumentando a vazao em picos. O PROCESSAMENTO permanece SEQUENCIAL: o handler
        // (HandleMessageAsync) e servido pela mesma fila de eventos do consumidor, entao nao ha
        // concorrencia real de processamento nem contencao adicional no banco. Constante (nao exposta em
        // RabbitMqOptions) porque essas options vivem no projeto Contracts, fora do escopo do worker.
        const ushort PrefetchCount = 10;
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: PrefetchCount, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, args) => HandleMessageAsync(channel, args, stoppingToken);

        await channel.BasicConsumeAsync(
            queue: _options.QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation("Worker consumindo fila {QueueName}.", _options.QueueName);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    /// <summary>
    /// Declara a topologia canonica de mensageria (mesmos valores/args do publisher do Campaigns.Api).
    /// Os argumentos das filas de retry PRECISAM bater com o publisher, senao o broker responde PRECONDITION_FAILED.
    /// </summary>
    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        // Exchange principal (direct, durable).
        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        // Fila principal: SEM args de DLX (mantem compatibilidade com a fila ja existente).
        await channel.QueueDeclareAsync(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: _options.QueueName,
            exchange: _options.ExchangeName,
            routingKey: _options.RoutingKey,
            cancellationToken: cancellationToken);

        // Retry exchange (direct, durable).
        await channel.ExchangeDeclareAsync(
            exchange: _options.RetryExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        // Fila de retry de 10s: apos o TTL faz dead-letter de volta para a exchange/fila principal.
        await channel.QueueDeclareAsync(
            queue: _options.Retry10sQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = _options.Retry10sTtlMs,
                ["x-dead-letter-exchange"] = _options.ExchangeName,
                ["x-dead-letter-routing-key"] = _options.RoutingKey
            },
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: _options.Retry10sQueue,
            exchange: _options.RetryExchange,
            routingKey: _options.Retry10sQueue,
            cancellationToken: cancellationToken);

        // Fila de retry de 60s.
        await channel.QueueDeclareAsync(
            queue: _options.Retry60sQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = _options.Retry60sTtlMs,
                ["x-dead-letter-exchange"] = _options.ExchangeName,
                ["x-dead-letter-routing-key"] = _options.RoutingKey
            },
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: _options.Retry60sQueue,
            exchange: _options.RetryExchange,
            routingKey: _options.Retry60sQueue,
            cancellationToken: cancellationToken);

        // Dead-letter exchange + fila.
        await channel.ExchangeDeclareAsync(
            exchange: _options.DeadLetterExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: _options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: _options.DeadLetterQueue,
            exchange: _options.DeadLetterExchange,
            routingKey: _options.DeadLetterRoutingKey,
            cancellationToken: cancellationToken);

        // Exchange de notificacoes (fanout, durable). Best-effort para tempo real (SignalR na Web).
        // NAO declaramos fila aqui: o consumidor (Web) cria a propria fila e faz o bind. Um fanout
        // sem fila apenas descarta a mensagem, sem erro. Declaracao idempotente.
        await channel.ExchangeDeclareAsync(
            exchange: _options.NotificationsExchange,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
    }

    private async Task HandleMessageAsync(IChannel channel, BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        // Copia o body enquanto o buffer do delivery ainda e valido (necessario para eventual republish).
        var body = args.Body.ToArray();
        var traceparent = GetHeaderString(args.BasicProperties, TraceParentHeader);
        var attempts = GetAttempts(args.BasicProperties);

        DoacaoRecebidaEvent? donationEvent;
        try
        {
            donationEvent = JsonSerializer.Deserialize<DoacaoRecebidaEvent>(body);
        }
        catch (JsonException ex)
        {
            // JSON invalido nunca sera reprocessado com sucesso: envia direto para a DLQ (sem requeue infinito).
            logger.LogWarning(ex, "Mensagem de doacao com JSON invalido. Enviando para a dead-letter queue.");
            await PublishAsync(channel, _options.DeadLetterExchange, _options.DeadLetterRoutingKey, body, attempts, traceparent, stoppingToken);
            DeadLetterMessages.Inc();
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            return;
        }

        if (donationEvent is null)
        {
            logger.LogWarning("Mensagem de doacao vazia ou invalida. Confirmando para evitar loop.");
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            return;
        }

        using var activity = StartActivity(traceparent, donationEvent.CorrelationId);

        try
        {
            DoacaoProcessadaNotification? notification;
            using (ProcessingDuration.NewTimer())
            {
                notification = await ProcessDonationAsync(donationEvent, stoppingToken);
            }

            // Ack somente apos o commit da transacao (ack-after-commit).
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);

            // Notificacao em tempo real: publicada FORA do delegate transacional (apos o processamento
            // confirmado e o ack) e SOMENTE quando a doacao foi realmente processada (nao em dedup/rejeicao).
            // Best-effort: qualquer falha aqui e apenas logada e NAO afeta o ack nem o processamento.
            if (notification is not null)
            {
                await PublishNotificationAsync(channel, notification, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            var newAttempts = attempts + 1;

            if (newAttempts < _options.MaxAttempts)
            {
                // Falha transitoria: reenfileira via retry exchange. O delay vem do TTL da fila de retry,
                // que faz dead-letter de volta para a fila principal quando o TTL expira.
                var retryRoutingKey = newAttempts == 1 ? _options.Retry10sQueue : _options.Retry60sQueue;
                logger.LogWarning(
                    ex,
                    "Falha transitoria ao processar doacao. Tentativa {NewAttempts}/{MaxAttempts}. Reenfileirando em {RetryQueue}. CorrelationId={CorrelationId}",
                    newAttempts,
                    _options.MaxAttempts,
                    retryRoutingKey,
                    donationEvent.CorrelationId);

                await PublishAsync(channel, _options.RetryExchange, retryRoutingKey, body, newAttempts, traceparent, stoppingToken);
            }
            else
            {
                // Esgotou as tentativas: dead-letter definitivo.
                logger.LogError(
                    ex,
                    "Doacao excedeu {MaxAttempts} tentativas. Enviando para a dead-letter queue. CorrelationId={CorrelationId}",
                    _options.MaxAttempts,
                    donationEvent.CorrelationId);

                await PublishAsync(channel, _options.DeadLetterExchange, _options.DeadLetterRoutingKey, body, newAttempts, traceparent, stoppingToken);
                DeadLetterMessages.Inc();
            }

            // Remove a mensagem original apos reencaminha-la (retry ou DLQ).
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
        }
    }

    /// <summary>Resultado do processamento, usado para incrementar metricas APOS o commit.</summary>
    private enum ProcessOutcome
    {
        None,
        Processed,
        Rejected
    }

    /// <returns>
    /// A notificacao a publicar quando (e somente quando) a doacao foi processada com sucesso; caso
    /// contrario (dedup, rejeicao, doacao/campanha inexistente) retorna null.
    /// </returns>
    private async Task<DoacaoProcessadaNotification?> ProcessDonationAsync(DoacaoRecebidaEvent donationEvent, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        var now = DateTimeOffset.UtcNow;

        // O EnableRetryOnFailure (ver Program.cs) instala uma execution strategy que NAO permite
        // BeginTransactionAsync manual sem passar por strategy.ExecuteAsync (lanca InvalidOperationException).
        // Por isso TODO o bloco transacional (dedup, guards, incremento atomico, upsert do read model,
        // MarkAsProcessed, insert em processed_messages, SaveChanges, Commit) roda dentro do delegate abaixo.
        //
        // SEGURANCA DA RE-EXECUCAO (idempotencia): se uma falha transitoria ocorrer ANTES do commit, a
        // transacao inteira e revertida (incremento em campaigns, upsert em campaign_stats e o insert em
        // processed_messages voltam atras). A dedup por ProcessedMessages so passa a valer DEPOIS do commit;
        // logo, numa re-execucao a strategy parte de um estado limpo e reprocessar e CORRETO — o total nao
        // e dobrado porque o incremento e o upsert so acontecem no caminho de sucesso, na mesma transacao
        // que sera comitada uma unica vez. O ack ao RabbitMQ so ocorre no caller, APOS este metodo retornar
        // (ou seja, apos o commit confirmado).
        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = ProcessOutcome.None;
        DoacaoProcessadaNotification? notification = null;

        await strategy.ExecuteAsync(async () =>
        {
            // Numa re-execucao da strategy o change tracker ainda reteria entidades da tentativa anterior
            // (donation carregada, ProcessedMessage adicionada). Limpa para reprocessar de um estado limpo.
            db.ChangeTracker.Clear();
            outcome = ProcessOutcome.None;
            // Reset por re-execucao: so sera preenchida no caminho de sucesso comitado.
            notification = null;

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // Idempotencia: se o evento ja foi processado, nada a fazer (o caller confirma/ack).
                var alreadyProcessed = await db.ProcessedMessages
                    .AnyAsync(message => message.EventId == donationEvent.EventoId, cancellationToken);

                if (alreadyProcessed)
                {
                    logger.LogInformation(
                        "Evento {EventoId} ja processado (dedup). CorrelationId={CorrelationId}",
                        donationEvent.EventoId,
                        donationEvent.CorrelationId);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                var donation = await db.Donations.SingleOrDefaultAsync(
                    item => item.Id == donationEvent.DoacaoId,
                    cancellationToken);

                if (donation is null)
                {
                    logger.LogWarning(
                        "Doacao {DoacaoId} nao encontrada. Evento sera confirmado para evitar loop. CorrelationId={CorrelationId}",
                        donationEvent.DoacaoId,
                        donationEvent.CorrelationId);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                // Defesa em profundidade: se ja finalizada, registra a dedup e encerra.
                if (donation.Status is DonationStatus.Processada or DonationStatus.Rejeitada)
                {
                    logger.LogInformation(
                        "Doacao {DoacaoId} ja finalizada com status {Status}. CorrelationId={CorrelationId}",
                        donation.Id,
                        donation.Status,
                        donationEvent.CorrelationId);
                    MarkEventProcessed(db, donationEvent.EventoId, now);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                var campaign = await db.Campaigns.SingleOrDefaultAsync(
                    item => item.Id == donationEvent.CampanhaId,
                    cancellationToken);

                if (campaign is null)
                {
                    donation.MarkAsRejected(now);
                    MarkEventProcessed(db, donationEvent.EventoId, now);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                if (!campaign.CanReceiveDonation(now))
                {
                    donation.MarkAsRejected(now);
                    MarkEventProcessed(db, donationEvent.EventoId, now);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    outcome = ProcessOutcome.Rejected;
                    return;
                }

                // Incremento ATOMICO do total arrecadado: UPDATE ... SET valor = valor + @amount direto no banco,
                // dentro da MESMA transacao aberta acima. Evita o read-modify-write (lost update) quando ha
                // varios workers processando doacoes concorrentemente. Nao passa pelo change tracker; a campanha
                // carregada acima serve apenas para as validacoes (CanReceiveDonation).
                await db.Campaigns
                    .Where(c => c.Id == campaign.Id)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(c => c.ValorTotalArrecadado, c => c.ValorTotalArrecadado + donationEvent.ValorDoacao)
                            .SetProperty(c => c.AtualizadaEm, (DateTimeOffset?)now),
                        cancellationToken);

                // Read model campaign_stats: UPSERT atomico na MESMA transacao (rollback junto se algo falhar).
                // Se nao existe linha para a campanha, insere (Titulo/Meta da campanha, TotalArrecadado = valor,
                // DoacoesProcessadas = 1); se existe, incrementa TotalArrecadado += valor e DoacoesProcessadas += 1
                // e atualiza AtualizadoEm. ON CONFLICT torna a operacao atomica no Postgres (usado nos testes
                // de integracao com Testcontainers). Colunas em PascalCase entre aspas (convencao EF/Npgsql).
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO campaign_stats ("CampaignId", "Titulo", "MetaFinanceira", "TotalArrecadado", "DoacoesProcessadas", "AtualizadoEm")
                    VALUES ({campaign.Id}, {campaign.Titulo}, {campaign.MetaFinanceira}, {donationEvent.ValorDoacao}, 1, {now})
                    ON CONFLICT ("CampaignId") DO UPDATE SET
                        "TotalArrecadado" = campaign_stats."TotalArrecadado" + EXCLUDED."TotalArrecadado",
                        "DoacoesProcessadas" = campaign_stats."DoacoesProcessadas" + 1,
                        "AtualizadoEm" = EXCLUDED."AtualizadoEm",
                        "Titulo" = EXCLUDED."Titulo",
                        "MetaFinanceira" = EXCLUDED."MetaFinanceira";
                    """,
                    cancellationToken);

                donation.MarkAsProcessed(now);
                MarkEventProcessed(db, donationEvent.EventoId, now);

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                outcome = ProcessOutcome.Processed;

                // Total apos esta doacao: o incremento atomico foi aplicado no banco; em memoria a campanha
                // carregada ainda tem o total ANTERIOR, entao somamos o valor manualmente.
                var totalArrecadado = campaign.ValorTotalArrecadado + donationEvent.ValorDoacao;
                notification = new DoacaoProcessadaNotification(
                    DoacaoId: donation.Id,
                    CampanhaId: campaign.Id,
                    CampanhaTitulo: campaign.Titulo,
                    Valor: donationEvent.ValorDoacao,
                    TotalArrecadado: totalArrecadado,
                    MetaFinanceira: campaign.MetaFinanceira,
                    MetaAtingida: totalArrecadado >= campaign.MetaFinanceira,
                    ProcessadaEm: now);

                logger.LogInformation(
                    "Doacao {DoacaoId} processada para campanha {CampanhaId}. Valor={Valor} CorrelationId={CorrelationId}",
                    donation.Id,
                    campaign.Id,
                    donationEvent.ValorDoacao,
                    donationEvent.CorrelationId);
            }
            catch (DbUpdateException ex)
            {
                // Redelivery concorrente: dois consumidores passaram pela checagem e o segundo commit violou a PK
                // de processed_messages (event_id unico). Tratamos como "ja processado" -> caller confirma/ack,
                // sem reprocessar nem duplicar totais. A transacao e desfeita no dispose. Nao relancamos: a
                // strategy nao deve reexecutar (nao e falha transitoria) e o resultado ja e o desejado.
                logger.LogInformation(
                    ex,
                    "Evento {EventoId} ja processado por outro consumidor (violacao de unicidade em processed_messages). CorrelationId={CorrelationId}",
                    donationEvent.EventoId,
                    donationEvent.CorrelationId);
            }
        });

        // Metricas incrementadas APOS o commit bem-sucedido e FORA do delegate reexecutavel, para nao
        // contar em dobro caso a strategy tenha reexecutado o bloco por uma falha transitoria.
        switch (outcome)
        {
            case ProcessOutcome.Processed:
                ProcessedDonations.Inc();
                // notification e sempre nao-nulo no caminho Processed (preenchido junto com outcome).
                if (notification is not null)
                {
                    var amount = (double)notification.Valor;
                    var campanha = string.IsNullOrWhiteSpace(notification.CampanhaTitulo)
                        ? "(sem titulo)"
                        : notification.CampanhaTitulo;

                    ProcessedAmount.Inc(amount);
                    ProcessedByCampaign.WithLabels(campanha).Inc();
                    ProcessedAmountByCampaign.WithLabels(campanha).Inc(amount);
                }
                break;
            case ProcessOutcome.Rejected:
                RejectedDonations.Inc();
                break;
        }

        return notification;
    }

    /// <summary>
    /// Publica a notificacao de doacao processada no exchange fanout (routingKey vazia, persistente).
    /// Best-effort: qualquer falha e apenas logada como warning e NAO relancada, para nao afetar o ack
    /// nem o processamento. Reusa o IChannel do consumidor.
    /// </summary>
    private async Task PublishNotificationAsync(
        IChannel channel,
        DoacaoProcessadaNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(notification);
            var properties = new BasicProperties { Persistent = true };

            await channel.BasicPublishAsync(
                exchange: _options.NotificationsExchange,
                routingKey: string.Empty,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Falha ao publicar notificacao de doacao processada (best-effort). DoacaoId={DoacaoId} CampanhaId={CampanhaId}",
                notification.DoacaoId,
                notification.CampanhaId);
        }
    }

    private static void MarkEventProcessed(CampaignsDbContext db, Guid eventId, DateTimeOffset now)
    {
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            EventId = eventId,
            ProcessedAtUtc = now
        });
    }

    private static async Task PublishAsync(
        IChannel channel,
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        int attempts,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, object?>
        {
            [AttemptsHeader] = attempts
        };

        if (!string.IsNullOrEmpty(traceparent))
        {
            headers[TraceParentHeader] = traceparent;
        }

        var properties = new BasicProperties
        {
            Persistent = true,
            Headers = headers
        };

        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    private static Activity? StartActivity(string? traceparent, string correlationId)
    {
        Activity? activity;
        if (!string.IsNullOrEmpty(traceparent) && ActivityContext.TryParse(traceparent, null, out var parentContext))
        {
            activity = ActivitySource.StartActivity("processar-doacao", ActivityKind.Consumer, parentContext);
        }
        else
        {
            activity = ActivitySource.StartActivity("processar-doacao", ActivityKind.Consumer);
        }

        if (activity is not null && !string.IsNullOrEmpty(correlationId))
        {
            activity.SetTag("conexao.correlation_id", correlationId);
        }

        return activity;
    }

    private static int GetAttempts(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null || !properties.Headers.TryGetValue(AttemptsHeader, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            short shortValue => shortValue,
            byte[] bytes => int.TryParse(Encoding.UTF8.GetString(bytes), out var parsedBytes) ? parsedBytes : 0,
            string text => int.TryParse(text, out var parsedText) ? parsedText : 0,
            _ => 0
        };
    }

    private static string? GetHeaderString(IReadOnlyBasicProperties properties, string key)
    {
        if (properties.Headers is null || !properties.Headers.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => value.ToString()
        };
    }
}
