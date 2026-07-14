using ConexaoSolidaria.Campaigns.Api.Data;
using ConexaoSolidaria.Campaigns.Api.Domain;
using ConexaoSolidaria.Campaigns.Api.Repositories;
using ConexaoSolidaria.Campaigns.Api.Responses;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Services;

public interface ICampaignService
{
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CampaignSearchResult<CampanhaResponse>> SearchAsync(
        string? term,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default);

    Task<Campaign> CreateAsync(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CancellationToken cancellationToken);

    Task<Campaign?> UpdateAsync(
        Guid id,
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CancellationToken cancellationToken);
}

public sealed class CampaignService(
    CampaignsDbContext db,
    ICampaignSearchRepository searchRepository) : ICampaignService
{
    public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task<CampaignSearchResult<CampanhaResponse>> SearchAsync(
        string? term,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return new CampaignSearchResult<CampanhaResponse>([], 0);
        }

        page = Math.Max(page, 1);
        pageSize = pageSize < 1 ? 10 : Math.Clamp(pageSize, 1, 100);

        var result = await searchRepository.SearchAsync(term.Trim(), page, pageSize, cancellationToken);

        return new CampaignSearchResult<CampanhaResponse>(
            result.Items.Select(ToResponse).ToList(),
            result.Total);
    }

    public async Task<Campaign> CreateAsync(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CancellationToken cancellationToken)
    {
        var campaign = Campaign.Create(
            titulo,
            descricao,
            dataInicio,
            dataFim,
            metaFinanceira,
            status,
            DateTimeOffset.UtcNow);

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
        await searchRepository.IndexAsync(campaign, cancellationToken);

        return campaign;
    }

    public async Task<Campaign?> UpdateAsync(
        Guid id,
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (campaign is null)
        {
            return null;
        }

        campaign.Update(
            titulo,
            descricao,
            dataInicio,
            dataFim,
            metaFinanceira,
            status,
            DateTimeOffset.UtcNow);

        await db.SaveChangesAsync(cancellationToken);
        await searchRepository.IndexAsync(campaign, cancellationToken);

        return campaign;
    }

    private static CampanhaResponse ToResponse(CampaignSearchDocument campaign)
    {
        return new CampanhaResponse(
            campaign.Id,
            campaign.Titulo,
            campaign.Descricao,
            campaign.DataInicio,
            campaign.DataFim,
            campaign.MetaFinanceira,
            campaign.ValorTotalArrecadado,
            campaign.Status);
    }
}
