using ConexaoSolidaria.Campaigns.Api.Repositories;
using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.IntegrationTests.Infrastructure;

/// <summary>
/// Substituto do <see cref="ICampaignSearchRepository"/> nos testes: evita a dependencia de um
/// Elasticsearch real. A indexacao vira no-op (a implementacao real lanca quando o ES esta ausente,
/// o que quebraria a criacao de campanhas) e a busca cai no fallback de PostgreSQL do CampaignService
/// ao retornar vazio/lancar. Aqui apenas devolvemos vazio para nao interferir.
/// </summary>
public sealed class FakeCampaignSearchRepository : ICampaignSearchRepository
{
    public Task<bool> EnsureIndexAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task IndexAsync(Campaign campaign, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task IndexManyAsync(IReadOnlyCollection<Campaign> campaigns, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<CampaignSearchResult<CampaignSearchDocument>> SearchAsync(
        string term,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CampaignSearchResult<CampaignSearchDocument>([], 0));
    }
}
