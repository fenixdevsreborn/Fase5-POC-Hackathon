using System.Text.Json;
using ConexaoSolidaria.Shared.Events;
using ConexaoSolidaria.Shared.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ConexaoSolidaria.Campaigns.Api.Messaging;

public sealed class RabbitMqDonationEventPublisher(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqDonationEventPublisher> logger) : IDonationEventPublisher
{
    private readonly RabbitMqOptions _options = options.Value;

    public async Task PublishAsync(DoacaoRecebidaEvent donationEvent, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

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

        var body = JsonSerializer.SerializeToUtf8Bytes(donationEvent);

        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: _options.RoutingKey,
            body: body,
            cancellationToken: cancellationToken);

        logger.LogInformation(
            "Evento DoacaoRecebidaEvent publicado. EventoId={EventoId} DoacaoId={DoacaoId}",
            donationEvent.EventoId,
            donationEvent.DoacaoId);
    }
}
