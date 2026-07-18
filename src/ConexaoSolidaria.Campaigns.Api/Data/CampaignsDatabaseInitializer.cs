using ConexaoSolidaria.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Data;

public static class CampaignsDatabaseInitializer
{
    /// <summary>
    /// Prepara o schema no startup da API. Comportamento controlado por <c>Migrations:RunOnStartup</c>:
    /// <list type="bullet">
    /// <item>default/"true" (dev, compose, testes): a API e DONA do schema e APLICA as
    /// migrations (<c>MigrateAsync</c>).</item>
    /// <item>"false" (k8s, onde um Job dedicado roda as migrations com RunMigrationsOnly): a API NAO
    /// migra; apenas AGUARDA o schema ficar pronto (CanConnect + consulta a uma tabela real), igual
    /// ao Donations.Worker, evitando corrida de migracao entre replicas.</item>
    /// </list>
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CampaignsDatabaseInitializer");

        // Default true: mantem o comportamento atual em dev/compose/testes.
        var runOnStartup = !string.Equals(
            configuration["Migrations:RunOnStartup"],
            "false",
            StringComparison.OrdinalIgnoreCase);

        if (runOnStartup)
        {
            await MigrateAsync(db, logger);
        }
        else
        {
            await WaitForSchemaAsync(db, logger);
        }
    }

    /// <summary>
    /// Aplica as migrations com retentativas. Usado no startup (quando RunOnStartup) e pelo Job do
    /// k8s (RunMigrationsOnly), que aplica e encerra o processo.
    /// </summary>
    public static async Task MigrateAsync(CampaignsDbContext db, ILogger logger)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                // A Campaigns.Api e a DONA do schema do 'campaignsdb': aplica as migrations reais
                // (campaigns, donations, outbox_messages, donation_idempotency_keys, processed_messages,
                // campaign_stats). O Donations.Worker apenas AGUARDA o schema ficar pronto, nao migra
                // (evita corrida de dois processos migrando o mesmo banco).
                await db.Database.MigrateAsync();
                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Banco de campanhas indisponivel. Tentativa {Attempt}/10.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    /// <summary>
    /// Nao migra: aguarda o banco responder E as tabelas existirem (migrations ja aplicadas por um Job
    /// externo). Consulta uma tabela real num try/catch; enquanto o schema nao estiver pronto, retenta.
    /// </summary>
    private static async Task WaitForSchemaAsync(CampaignsDbContext db, ILogger logger)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                if (await db.Database.CanConnectAsync())
                {
                    // Se as tabelas ja existem, esta consulta funciona; se ainda nao migraram, lanca e retenta.
                    await db.Campaigns.AnyAsync();
                    logger.LogInformation("Schema do banco de campanhas pronto (migracao externa/Job).");
                    return;
                }

                logger.LogWarning("Banco de campanhas ainda nao aceita conexao. Tentativa {Attempt}/10.", attempt);
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Schema do banco de campanhas ainda nao pronto. Tentativa {Attempt}/10.", attempt);
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}
