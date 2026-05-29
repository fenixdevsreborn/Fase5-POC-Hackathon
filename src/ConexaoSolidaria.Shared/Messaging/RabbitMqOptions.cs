namespace ConexaoSolidaria.Shared.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; init; } = "localhost";

    public int Port { get; init; } = 5672;

    public string UserName { get; init; } = "guest";

    public string Password { get; init; } = "guest";

    public string ExchangeName { get; init; } = "conexao-solidaria";

    public string QueueName { get; init; } = "doacoes-recebidas";

    public string RoutingKey { get; init; } = "doacao.recebida";
}
