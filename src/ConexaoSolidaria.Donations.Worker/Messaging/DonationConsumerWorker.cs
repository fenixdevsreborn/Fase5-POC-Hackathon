using System.Text.Json;
using ConexaoSolidaria.Donations.Worker.Data;
using ConexaoSolidaria.Donations.Worker.Domain;
using ConexaoSolidaria.Shared.Events;
using ConexaoSolidaria.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ConexaoSolidaria.Donations.Worker.Messaging;

public sealed class DonationConsumerWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<DonationConsumerWorker> logger) : BackgroundService
{
    private static readonly Counter ProcessedDonations = Metrics.CreateCounter(
        "conexao_donations_processed_total",
        "Quantidade de doacoes processadas pelo worker.");

    private static readonly Counter RejectedDonations = Metrics.CreateCounter(
        "conexao_donations_rejected_total",
        "Quantidade de tentativas de doacao rejeitadas por campanha encerrada ou cancelada.");

    private readonly RabbitMqOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Donations Worker iniciado. RabbitMQ={HostName}:{Port} Exchange={ExchangeName} Queue={QueueName} RoutingKey={RoutingKey}",
            _options.HostName,
            _options.Port,
            _options.ExchangeName,
            _options.QueueName,
            _options.RoutingKey);

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
        logger.LogInformation(
            "Conectando ao RabbitMQ em {HostName}:{Port}.",
            _options.HostName,
            _options.Port);

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        logger.LogInformation("Conexao com RabbitMQ estabelecida.");

        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        logger.LogInformation("Canal RabbitMQ criado.");

        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);
        logger.LogInformation(
            "Exchange garantida. Exchange={ExchangeName} Type={ExchangeType} Durable={Durable}",
            _options.ExchangeName,
            ExchangeType.Direct,
            true);

        await channel.QueueDeclareAsync(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken);
        logger.LogInformation(
            "Fila garantida. Queue={QueueName} Durable={Durable}",
            _options.QueueName,
            true);

        await channel.QueueBindAsync(
            queue: _options.QueueName,
            exchange: _options.ExchangeName,
            routingKey: _options.RoutingKey,
            cancellationToken: stoppingToken);
        logger.LogInformation(
            "Bind garantido. Queue={QueueName} Exchange={ExchangeName} RoutingKey={RoutingKey}",
            _options.QueueName,
            _options.ExchangeName,
            _options.RoutingKey);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);
        logger.LogInformation("QoS configurado. PrefetchCount={PrefetchCount}", 1);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            logger.LogInformation(
                "Mensagem recebida do RabbitMQ. DeliveryTag={DeliveryTag} Redelivered={Redelivered} Exchange={Exchange} RoutingKey={RoutingKey} BodyBytes={BodyBytes}",
                args.DeliveryTag,
                args.Redelivered,
                args.Exchange,
                args.RoutingKey,
                args.Body.Length);

            try
            {
                var donationEvent = JsonSerializer.Deserialize<DoacaoRecebidaEvent>(args.Body.Span);
                if (donationEvent is null)
                {
                    logger.LogWarning(
                        "Mensagem de doacao vazia ou invalida. Confirmando para remover da fila. DeliveryTag={DeliveryTag}",
                        args.DeliveryTag);
                    await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    logger.LogInformation("Mensagem invalida confirmada. DeliveryTag={DeliveryTag}", args.DeliveryTag);
                    return;
                }

                logger.LogInformation(
                    "Evento de doacao desserializado. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId} DoadorId={DoadorId} DoadorEmail={DoadorEmail} Valor={Valor} OcorridaEm={OcorridaEm}",
                    donationEvent.EventoId,
                    donationEvent.DoacaoId,
                    donationEvent.CampanhaId,
                    donationEvent.DoadorId,
                    donationEvent.DoadorEmail,
                    donationEvent.ValorDoacao,
                    donationEvent.OcorridaEm);

                await ProcessDonationAsync(donationEvent, stoppingToken);
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                logger.LogInformation(
                    "Mensagem processada e confirmada no RabbitMQ. DeliveryTag={DeliveryTag} EventoId={EventoId} DoacaoId={DoacaoId}",
                    args.DeliveryTag,
                    donationEvent.EventoId,
                    donationEvent.DoacaoId);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(
                    ex,
                    "Mensagem de doacao com JSON invalido. Removendo da fila. DeliveryTag={DeliveryTag}",
                    args.DeliveryTag);
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                logger.LogInformation("Mensagem com JSON invalido rejeitada sem requeue. DeliveryTag={DeliveryTag}", args.DeliveryTag);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Erro ao processar mensagem de doacao. A mensagem voltara para a fila. DeliveryTag={DeliveryTag}",
                    args.DeliveryTag);
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
                logger.LogWarning("Mensagem rejeitada com requeue. DeliveryTag={DeliveryTag}", args.DeliveryTag);
            }
        };

        await channel.BasicConsumeAsync(
            queue: _options.QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation(
            "Worker consumindo fila {QueueName}. Aguardando eventos de doacao...",
            _options.QueueName);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task ProcessDonationAsync(DoacaoRecebidaEvent donationEvent, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Iniciando processamento da doacao. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId} Valor={Valor}",
            donationEvent.EventoId,
            donationEvent.DoacaoId,
            donationEvent.CampanhaId,
            donationEvent.ValorDoacao);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerCampaignsDbContext>();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        logger.LogInformation(
            "Transacao iniciada para doacao. EventoId={EventoId} DoacaoId={DoacaoId}",
            donationEvent.EventoId,
            donationEvent.DoacaoId);

        var donation = await db.Donations.SingleOrDefaultAsync(
            item => item.Id == donationEvent.DoacaoId,
            cancellationToken);

        if (donation is null)
        {
            logger.LogWarning(
                "Doacao nao encontrada no banco. Evento sera confirmado para evitar loop. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId}",
                donationEvent.EventoId,
                donationEvent.DoacaoId,
                donationEvent.CampanhaId);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Transacao finalizada sem alteracoes porque a doacao nao foi encontrada. EventoId={EventoId} DoacaoId={DoacaoId}",
                donationEvent.EventoId,
                donationEvent.DoacaoId);
            return;
        }

        logger.LogInformation(
            "Doacao localizada. DoacaoId={DoacaoId} StatusAtual={StatusAtual} ValorBanco={ValorBanco} DoadorEmail={DoadorEmail} CriadaEm={CriadaEm}",
            donation.Id,
            donation.Status,
            donation.Valor,
            donation.DoadorEmail,
            donation.CriadaEm);

        if (donation.Status is DonationStatus.Processada or DonationStatus.Rejeitada)
        {
            logger.LogInformation(
                "Doacao ja foi finalizada. Nenhuma alteracao sera feita. EventoId={EventoId} DoacaoId={DoacaoId} Status={Status}",
                donationEvent.EventoId,
                donation.Id,
                donation.Status);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Transacao finalizada para doacao ja encerrada. EventoId={EventoId} DoacaoId={DoacaoId}",
                donationEvent.EventoId,
                donation.Id);
            return;
        }

        var campaign = await db.Campaigns.SingleOrDefaultAsync(
            item => item.Id == donationEvent.CampanhaId,
            cancellationToken);

        if (campaign is null)
        {
            logger.LogWarning(
                "Campanha nao encontrada. Doacao sera marcada como rejeitada. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId}",
                donationEvent.EventoId,
                donation.Id,
                donationEvent.CampanhaId);
            donation.MarkAsRejected(now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Doacao rejeitada por campanha inexistente. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId}",
                donationEvent.EventoId,
                donation.Id,
                donationEvent.CampanhaId);
            return;
        }

        logger.LogInformation(
            "Campanha localizada. CampanhaId={CampanhaId} Titulo={Titulo} Status={Status} DataFim={DataFim} ValorAtual={ValorAtual} Meta={Meta}",
            campaign.Id,
            campaign.Titulo,
            campaign.Status,
            campaign.DataFim,
            campaign.ValorTotalArrecadado,
            campaign.MetaFinanceira);

        if (!campaign.CanReceiveDonation(now))
        {
            logger.LogWarning(
                "Campanha nao pode receber doacao. Doacao sera rejeitada. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId} Status={Status} DataFim={DataFim} AgoraUtc={AgoraUtc}",
                donationEvent.EventoId,
                donation.Id,
                campaign.Id,
                campaign.Status,
                campaign.DataFim,
                now);
            donation.MarkAsRejected(now);
            RejectedDonations.Inc();
            logger.LogInformation(
                "Metrica incrementada. Metric=conexao_donations_rejected_total DoacaoId={DoacaoId} CampanhaId={CampanhaId}",
                donation.Id,
                campaign.Id);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Doacao rejeitada e persistida. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId} StatusDoacao={StatusDoacao}",
                donationEvent.EventoId,
                donation.Id,
                campaign.Id,
                donation.Status);
            return;
        }

        var previousCampaignTotal = campaign.ValorTotalArrecadado;
        campaign.AddDonation(donationEvent.ValorDoacao);
        donation.MarkAsProcessed(now);
        ProcessedDonations.Inc();
        logger.LogInformation(
            "Doacao aplicada em memoria. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId} ValorDoacao={ValorDoacao} TotalAnterior={TotalAnterior} NovoTotal={NovoTotal}",
            donationEvent.EventoId,
            donation.Id,
            campaign.Id,
            donationEvent.ValorDoacao,
            previousCampaignTotal,
            campaign.ValorTotalArrecadado);

        logger.LogInformation(
            "Metrica incrementada. Metric=conexao_donations_processed_total DoacaoId={DoacaoId} CampanhaId={CampanhaId}",
            donation.Id,
            campaign.Id);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Alteracoes salvas no banco. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId}",
            donationEvent.EventoId,
            donation.Id,
            campaign.Id);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Doacao processada com sucesso. EventoId={EventoId} DoacaoId={DoacaoId} CampanhaId={CampanhaId} Valor={Valor} TotalAnterior={TotalAnterior} NovoTotal={NovoTotal} StatusDoacao={StatusDoacao}",
            donationEvent.EventoId,
            donation.Id,
            campaign.Id,
            donationEvent.ValorDoacao,
            previousCampaignTotal,
            campaign.ValorTotalArrecadado,
            donation.Status);
    }
}
