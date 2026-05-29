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
        "Quantidade de doacoes rejeitadas pelo worker.");

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
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await channel.QueueBindAsync(
            queue: _options.QueueName,
            exchange: _options.ExchangeName,
            routingKey: _options.RoutingKey,
            cancellationToken: stoppingToken);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                var donationEvent = JsonSerializer.Deserialize<DoacaoRecebidaEvent>(args.Body.Span);
                if (donationEvent is null)
                {
                    logger.LogWarning("Mensagem de doacao vazia ou invalida.");
                    await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    return;
                }

                await ProcessDonationAsync(donationEvent, stoppingToken);
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Mensagem de doacao com JSON invalido. Removendo da fila.");
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar mensagem de doacao. A mensagem voltara para a fila.");
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(
            queue: _options.QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation("Worker consumindo fila {QueueName}.", _options.QueueName);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task ProcessDonationAsync(DoacaoRecebidaEvent donationEvent, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerCampaignsDbContext>();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var donation = await db.Donations.SingleOrDefaultAsync(
            item => item.Id == donationEvent.DoacaoId,
            cancellationToken);

        if (donation is null)
        {
            logger.LogWarning("Doacao {DoacaoId} nao encontrada. Evento sera confirmado para evitar loop.", donationEvent.DoacaoId);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (donation.Status is DonationStatus.Processada or DonationStatus.Rejeitada)
        {
            logger.LogInformation("Doacao {DoacaoId} ja foi finalizada com status {Status}.", donation.Id, donation.Status);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var campaign = await db.Campaigns.SingleOrDefaultAsync(
            item => item.Id == donationEvent.CampanhaId,
            cancellationToken);

        if (campaign is null || !campaign.CanReceiveDonation(now))
        {
            donation.MarkAsRejected(now);
            RejectedDonations.Inc();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        campaign.AddDonation(donationEvent.ValorDoacao);
        donation.MarkAsProcessed(now);
        ProcessedDonations.Inc();

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Doacao {DoacaoId} processada para campanha {CampanhaId}. Valor={Valor}",
            donation.Id,
            campaign.Id,
            donationEvent.ValorDoacao);
    }
}
