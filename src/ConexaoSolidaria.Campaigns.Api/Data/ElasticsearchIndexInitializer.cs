using ConexaoSolidaria.Campaigns.Api.Repositories;
using ConexaoSolidaria.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Data;

/// <summary>
/// Garante, no startup, que o indice de busca existe com o mapeamento/analisadores pt-BR. Quando o
/// indice e criado agora (primeira subida ou apos ser dropado), faz o backfill de TODAS as campanhas
/// do Postgres (fonte da verdade) para o Elasticsearch, deixando a busca utilizavel de imediato.
///
/// Tudo aqui e best-effort: o Elasticsearch NAO e critico (a busca degrada para o Postgres). Qualquer
/// falha e apenas logada e nao derruba a API.
/// </summary>
public static class ElasticsearchIndexInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("ElasticsearchIndexInitializer");

        try
        {
            var search = scope.ServiceProvider.GetRequiredService<ICampaignSearchRepository>();

            var created = await search.EnsureIndexAsync(CancellationToken.None);
            if (!created)
            {
                // Indice ja existia: nada a fazer (evita reindexar tudo a cada startup).
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
            var campaigns = await db.Campaigns.AsNoTracking().ToListAsync();

            if (campaigns.Count == 0)
            {
                return;
            }

            logger.LogInformation("Indice recem-criado: iniciando backfill de {Count} campanha(s).", campaigns.Count);
            await search.IndexManyAsync(campaigns, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Falha ao preparar o indice do Elasticsearch no startup. A busca seguira com o fallback do PostgreSQL.");
        }
    }
}
