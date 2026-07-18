using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace ConexaoSolidaria.Contracts.Messaging;

/// <summary>
/// Constroi a <see cref="ConnectionFactory"/> do RabbitMQ conciliando duas origens de configuracao:
/// <list type="bullet">
/// <item>Aspire: injeta uma connection string AMQP em <c>ConnectionStrings:messaging</c>
/// (URI no formato <c>amqp://user:pass@host:port</c>). Quando presente, tem prioridade.</item>
/// <item>docker-compose / k8s: usa os campos discretos de <see cref="RabbitMqOptions"/>
/// (HostName/Port/UserName/Password), preservando o comportamento atual.</item>
/// </list>
/// Isso permite que o mesmo binario rode sob o compose e sob o k8s sem alteracao de codigo.
/// </summary>
public static class RabbitMqConnectionFactoryBuilder
{
    /// <summary>
    /// Nome da connection string AMQP injetada pelo Aspire (<c>builder.AddRabbitMQ("messaging")</c>).
    /// </summary>
    public const string ConnectionStringName = "messaging";

    public static ConnectionFactory Build(IConfiguration configuration, RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            // Origem Aspire: a URI carrega host, porta e credenciais geradas automaticamente.
            return new ConnectionFactory
            {
                Uri = new Uri(connectionString)
            };
        }

        // Origem compose/k8s: campos discretos das RabbitMqOptions.
        return new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            UserName = options.UserName,
            Password = options.Password
        };
    }
}
