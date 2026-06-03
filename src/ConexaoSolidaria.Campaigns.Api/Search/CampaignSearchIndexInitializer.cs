using ConexaoSolidaria.Campaigns.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Search;

public static class CampaignSearchIndexInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
        var searchService = scope.ServiceProvider.GetRequiredService<ICampaignSearchService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CampaignSearchIndexInitializer");

        const int maxAttempts = 10;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await searchService.EnsureReadyAsync(CancellationToken.None);

                var campaigns = await db.Campaigns
                    .AsNoTracking()
                    .ToListAsync(CancellationToken.None);

                foreach (var campaign in campaigns)
                {
                    await searchService.IndexAsync(campaign, CancellationToken.None);
                }

                logger.LogInformation("Indice de campanhas sincronizado no Elasticsearch. Total={Total}.", campaigns.Count);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Elasticsearch indisponivel para sincronizacao inicial. Tentativa {Attempt}/{MaxAttempts}.",
                    attempt,
                    maxAttempts);

                if (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
        }
    }
}
