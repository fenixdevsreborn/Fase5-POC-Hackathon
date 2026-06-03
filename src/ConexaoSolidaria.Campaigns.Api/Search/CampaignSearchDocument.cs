using ConexaoSolidaria.Campaigns.Api.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Search;

public sealed record CampaignSearchDocument(
    Guid Id,
    string Titulo,
    string Status,
    DateTimeOffset DataFim)
{
    public static CampaignSearchDocument From(Campaign campaign)
    {
        return new CampaignSearchDocument(
            campaign.Id,
            campaign.Titulo,
            campaign.Status.ToString(),
            campaign.DataFim);
    }
}
