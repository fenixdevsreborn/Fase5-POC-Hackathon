using ConexaoSolidaria.Campaigns.Api.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Search;

public interface ICampaignSearchService
{
    Task EnsureReadyAsync(CancellationToken cancellationToken);

    Task IndexAsync(Campaign campaign, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Guid>?> SearchIdsByTitleAsync(string title, CancellationToken cancellationToken);
}
