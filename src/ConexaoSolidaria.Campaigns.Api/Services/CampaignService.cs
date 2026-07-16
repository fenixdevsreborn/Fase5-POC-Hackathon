using ConexaoSolidaria.Shared.Domain;
using ConexaoSolidaria.Shared.Persistence;
using ConexaoSolidaria.Campaigns.Api.Repositories;
using ConexaoSolidaria.Campaigns.Api.Responses;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;

namespace ConexaoSolidaria.Campaigns.Api.Services;

/// <summary>Acoes de ciclo de vida da campanha expostas ao gestor.</summary>
public enum CampaignTransition
{
    Ativar,
    Concluir,
    Cancelar
}

public interface ICampaignService
{
    /// <summary>
    /// Leitura publica projetada direto para <see cref="CampanhaResponse"/> (AsNoTracking, sem
    /// materializar a entidade rica rastreada). Retorna null quando a campanha nao existe.
    /// </summary>
    Task<CampanhaResponse?> GetResponseByIdAsync(Guid id, CancellationToken cancellationToken);

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
        CampaignCategory categoria,
        CancellationToken cancellationToken);

    Task<Campaign?> UpdateAsync(
        Guid id,
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CampaignCategory categoria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Aplica uma transicao de ciclo de vida (Ativar/Concluir/Cancelar) na campanha rastreada,
    /// delegando as regras ao dominio (DomainRuleException em transicao invalida). Retorna null
    /// quando a campanha nao existe.
    /// </summary>
    Task<Campaign?> TransitionAsync(
        Guid id,
        CampaignTransition action,
        CancellationToken cancellationToken);
}

public sealed class CampaignService : ICampaignService
{
    /// <summary>Chave do pipeline de resiliencia (circuit breaker) que protege o Elasticsearch.</summary>
    public const string SearchPipelineKey = "elasticsearch-search";

    private readonly CampaignsDbContext db;
    private readonly ICampaignSearchRepository searchRepository;
    private readonly ILogger<CampaignService> logger;
    private readonly ResiliencePipeline searchPipeline;

    public CampaignService(
        CampaignsDbContext db,
        ICampaignSearchRepository searchRepository,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<CampaignService> logger)
    {
        this.db = db;
        this.searchRepository = searchRepository;
        this.logger = logger;
        this.searchPipeline = pipelineProvider.GetPipeline(SearchPipelineKey);
    }

    public async Task<CampanhaResponse?> GetResponseByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // Projecao direta para o DTO publico: AsNoTracking + Select, sem materializar a entidade rica.
        var response = await db.Campaigns
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new CampanhaResponse(
                item.Id,
                item.Titulo,
                item.Descricao,
                item.DataInicio,
                item.DataFim,
                item.MetaFinanceira,
                item.ValorTotalArrecadado,
                item.Status,
                item.Categoria,
                0))
            .SingleOrDefaultAsync(cancellationToken);

        if (response is null)
        {
            return null;
        }

        var enriched = await EnrichWithDonorCountsAsync([response], cancellationToken);
        return enriched[0];
    }

    public async Task<CampaignSearchResult<CampanhaResponse>> SearchAsync(
        string? term,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = pageSize < 1 ? 10 : Math.Clamp(pageSize, 1, 100);

        // Sem termo = "listar todas" (ex.: painel do gestor e vitrine). Vem direto do Postgres
        // (fonte da verdade), paginado; o Elasticsearch so entra quando ha termo de busca.
        if (string.IsNullOrWhiteSpace(term))
        {
            return await ListFromDatabaseAsync(page, pageSize, cancellationToken);
        }

        var normalizedTerm = term.Trim();

        try
        {
            // Circuit breaker: a chamada ao ES passa pelo pipeline. Apos N falhas o circuito abre e
            // ExecuteAsync passa a lancar BrokenCircuitException IMEDIATAMENTE (sem martelar o ES) por
            // um periodo, caindo direto no fallback do Postgres abaixo.
            var result = await searchPipeline.ExecuteAsync(
                async token => await searchRepository.SearchAsync(normalizedTerm, page, pageSize, token),
                cancellationToken);

            var enriched = await EnrichWithDonorCountsAsync(
                result.Items.Select(ToResponse).ToList(),
                cancellationToken);

            return new CampaignSearchResult<CampanhaResponse>(enriched, result.Total);
        }
        catch (BrokenCircuitException)
        {
            // Circuito aberto: nem tentamos o ES, vamos direto ao Postgres.
            logger.LogWarning(
                "Circuito do Elasticsearch ABERTO. Servindo a busca '{Term}' direto do PostgreSQL.",
                normalizedTerm);

            return await SearchInDatabaseAsync(normalizedTerm, page, pageSize, cancellationToken);
        }
        catch (Exception ex)
        {
            // Degradacao graciosa: se o Elasticsearch falhar/timeout, cai para uma busca no PostgreSQL
            // (ILIKE em Titulo/Descricao) com a mesma paginacao e formato de resultado.
            logger.LogWarning(
                ex,
                "Busca no Elasticsearch indisponivel para o termo '{Term}'. Aplicando fallback para PostgreSQL.",
                normalizedTerm);

            return await SearchInDatabaseAsync(normalizedTerm, page, pageSize, cancellationToken);
        }
    }

    // Lista todas as campanhas (sem filtro de termo) do Postgres, ordenadas pela mais recente.
    private async Task<CampaignSearchResult<CampanhaResponse>> ListFromDatabaseAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Campaigns.AsNoTracking();

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(campaign => campaign.CriadaEm)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(campaign => new CampanhaResponse(
                campaign.Id,
                campaign.Titulo,
                campaign.Descricao,
                campaign.DataInicio,
                campaign.DataFim,
                campaign.MetaFinanceira,
                campaign.ValorTotalArrecadado,
                campaign.Status,
                campaign.Categoria,
                0))
            .ToListAsync(cancellationToken);

        var enriched = await EnrichWithDonorCountsAsync(items, cancellationToken);
        return new CampaignSearchResult<CampanhaResponse>(enriched, total);
    }

    private async Task<CampaignSearchResult<CampanhaResponse>> SearchInDatabaseAsync(
        string term,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var pattern = $"%{term}%";

        var query = db.Campaigns
            .AsNoTracking()
            .Where(campaign =>
                EF.Functions.ILike(campaign.Titulo, pattern) ||
                EF.Functions.ILike(campaign.Descricao, pattern));

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(campaign => campaign.CriadaEm)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(campaign => new CampanhaResponse(
                campaign.Id,
                campaign.Titulo,
                campaign.Descricao,
                campaign.DataInicio,
                campaign.DataFim,
                campaign.MetaFinanceira,
                campaign.ValorTotalArrecadado,
                campaign.Status,
                campaign.Categoria,
                0))
            .ToListAsync(cancellationToken);

        var enriched = await EnrichWithDonorCountsAsync(items, cancellationToken);
        return new CampaignSearchResult<CampanhaResponse>(enriched, total);
    }

    public async Task<Campaign> CreateAsync(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CampaignCategory categoria,
        CancellationToken cancellationToken)
    {
        var campaign = Campaign.Create(
            titulo,
            descricao,
            dataInicio,
            dataFim,
            metaFinanceira,
            status,
            DateTimeOffset.UtcNow,
            categoria);

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
        await TryIndexAsync(campaign, cancellationToken);

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
        CampaignCategory categoria,
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
            DateTimeOffset.UtcNow,
            categoria);

        await db.SaveChangesAsync(cancellationToken);
        await TryIndexAsync(campaign, cancellationToken);

        return campaign;
    }

    // A indexacao no Elasticsearch e best-effort: o Postgres e a fonte da verdade e a busca ja
    // degrada para o Postgres (ver SearchAsync). Uma falha de indexacao NAO deve derrubar o
    // Create/Update (que ja persistiu no banco), evitando 500 por indisponibilidade/incompat do ES.
    private async Task TryIndexAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        try
        {
            await searchRepository.IndexAsync(campaign, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Falha ao indexar a campanha {CampaignId} no Elasticsearch; a operacao segue (a busca usa o fallback do PostgreSQL).",
                campaign.Id);
        }
    }

    public async Task<Campaign?> TransitionAsync(
        Guid id,
        CampaignTransition action,
        CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (campaign is null)
        {
            return null;
        }

        // Regras de transicao ficam no dominio; DomainRuleException vira 422 no handler global.
        switch (action)
        {
            case CampaignTransition.Ativar:
                campaign.Ativar();
                break;
            case CampaignTransition.Concluir:
                campaign.Concluir();
                break;
            case CampaignTransition.Cancelar:
                campaign.Cancelar();
                break;
        }

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
            campaign.Status,
            campaign.Categoria,
            0);
    }

    /// <summary>
    /// Preenche <see cref="CampanhaResponse.TotalDoadores"/> a partir do read model campaign_stats
    /// (populado pelo Donations.Worker). Uma unica consulta por lote; campanhas sem doacoes
    /// processadas ainda nao tem linha em campaign_stats e permanecem com 0.
    /// </summary>
    private async Task<IReadOnlyList<CampanhaResponse>> EnrichWithDonorCountsAsync(
        IReadOnlyList<CampanhaResponse> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var ids = items.Select(item => item.Id).ToList();

        var counts = await db.CampaignStats
            .AsNoTracking()
            .Where(stats => ids.Contains(stats.CampaignId))
            .ToDictionaryAsync(stats => stats.CampaignId, stats => stats.DoacoesProcessadas, cancellationToken);

        return items
            .Select(item => counts.TryGetValue(item.Id, out var total) ? item with { TotalDoadores = total } : item)
            .ToList();
    }
}
