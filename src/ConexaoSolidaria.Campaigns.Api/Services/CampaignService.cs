using ConexaoSolidaria.Shared.Domain;
using ConexaoSolidaria.Shared.Persistence;
using ConexaoSolidaria.Campaigns.Api.Repositories;
using ConexaoSolidaria.Campaigns.Api.Requests;
using ConexaoSolidaria.Campaigns.Api.Responses;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

    /// <summary>
    /// Cria a campanha. Lanca <see cref="DuplicateCampaignTitleException"/> (=&gt; 409) quando ja
    /// existe outra com o mesmo titulo, ignorando maiusculas/minusculas e espacos.
    /// </summary>
    Task<Campaign> CreateAsync(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CampaignCategory categoria,
        CancellationToken cancellationToken,
        string? imagem = null);

    /// <summary>
    /// Cria varias campanhas com sucesso parcial: cada item e persistido isoladamente e as falhas
    /// (titulo duplicado, regra de dominio) sao devolvidas por indice, sem derrubar o restante.
    /// </summary>
    Task<CriacaoEmLoteResponse> CreateManyAsync(
        IReadOnlyList<SalvarCampanhaRequest> campanhas,
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
        CancellationToken cancellationToken,
        string? imagem = null);

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
                0,
                item.Imagem))
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
                0,
                campaign.Imagem))
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
                0,
                campaign.Imagem))
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
        CancellationToken cancellationToken,
        string? imagem = null)
    {
        var campaign = await PersistirNovaAsync(
            titulo, descricao, dataInicio, dataFim, metaFinanceira, status, categoria, imagem, cancellationToken);

        await TryIndexAsync(campaign, cancellationToken);

        return campaign;
    }

    // Grava a campanha SEM indexar no Elasticsearch. Separado para que a criacao em lote pague o
    // custo da indexacao uma unica vez (bulk) em vez de uma vez por item — ver CreateManyAsync.
    private async Task<Campaign> PersistirNovaAsync(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CampaignCategory categoria,
        string? imagem,
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
            categoria,
            imagem);

        await GarantirTituloDisponivelAsync(campaign.TituloNormalizado, null, titulo, cancellationToken);

        db.Campaigns.Add(campaign);
        await SalvarTratandoTituloDuplicadoAsync(titulo, cancellationToken);

        return campaign;
    }

    public async Task<CriacaoEmLoteResponse> CreateManyAsync(
        IReadOnlyList<SalvarCampanhaRequest> campanhas,
        CancellationToken cancellationToken)
    {
        var criadas = new List<CampanhaResponse>();
        var erros = new List<CampanhaEmLoteErro>();
        var indexar = new List<Campaign>();

        // Duplicatas DENTRO do proprio lote nao chegariam ao banco na mesma transacao, entao sao
        // detectadas aqui: a primeira ocorrencia e criada e as seguintes viram erro do item.
        var titulosDoLote = new HashSet<string>(StringComparer.Ordinal);

        for (var indice = 0; indice < campanhas.Count; indice++)
        {
            var item = campanhas[indice];
            var titulo = item.Titulo ?? string.Empty;

            try
            {
                if (!titulosDoLote.Add(Campaign.NormalizarTitulo(titulo)))
                {
                    throw new DuplicateCampaignTitleException(titulo);
                }

                // PersistirNovaAsync (e nao CreateAsync): a indexacao fica para o fim, em uma
                // unica chamada bulk. Indexar item a item aqui fazia o lote pagar a latencia do
                // Elasticsearch N vezes — com o ES fora do ar, ~12s POR CAMPANHA (o timeout da
                // conexao), o que estourava o tempo de resposta em qualquer planilha real.
                var campaign = await PersistirNovaAsync(
                    titulo,
                    item.Descricao,
                    item.DataInicio,
                    item.DataFim,
                    item.MetaFinanceira,
                    item.Status,
                    item.Categoria,
                    item.Imagem,
                    cancellationToken);

                indexar.Add(campaign);
                criadas.Add(ToResponse(campaign));
            }
            catch (Exception ex) when (ex is DomainRuleException or DuplicateCampaignTitleException)
            {
                // Falha de um item nao invalida o lote inteiro; o gestor corrige so o que falhou.
                // A entidade rejeitada continua rastreada pelo contexto e envenenaria o proximo
                // SaveChanges, entao e descartada aqui.
                DescartarEntidadesPendentes();
                erros.Add(new CampanhaEmLoteErro(indice, titulo, ex.Message));

                logger.LogWarning(
                    "Item {Indice} do lote rejeitado ({Titulo}): {Motivo}", indice, titulo, ex.Message);
            }
        }

        await TryIndexManyAsync(indexar, cancellationToken);

        return new CriacaoEmLoteResponse(criadas, erros);
    }

    // Best-effort, mesma politica de TryIndexAsync: o Postgres e a fonte da verdade e a busca ja
    // degrada para ele, entao uma falha de indexacao nao pode derrubar um lote ja persistido.
    private async Task TryIndexManyAsync(
        IReadOnlyCollection<Campaign> campaigns,
        CancellationToken cancellationToken)
    {
        if (campaigns.Count == 0)
        {
            return;
        }

        try
        {
            await searchRepository.IndexManyAsync(campaigns, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Falha ao indexar {Count} campanha(s) do lote no Elasticsearch; a operacao segue (a busca usa o fallback do PostgreSQL).",
                campaigns.Count);
        }
    }

    // Remove do ChangeTracker campanhas adicionadas que nao chegaram a ser persistidas. Sem isso,
    // um item invalido no meio do lote seria reenviado (e falharia de novo) no SaveChanges seguinte.
    private void DescartarEntidadesPendentes()
    {
        foreach (var entry in db.ChangeTracker.Entries<Campaign>()
                     .Where(entry => entry.State == EntityState.Added)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // Pre-checagem para dar 409 com mensagem clara antes de tentar o INSERT. Nao substitui o
    // indice unico: entre esta consulta e o SaveChanges cabe uma requisicao concorrente, e por
    // isso SalvarTratandoTituloDuplicadoAsync tambem trata a violacao vinda do banco.
    private async Task GarantirTituloDisponivelAsync(
        string tituloNormalizado,
        Guid? ignorarId,
        string tituloOriginal,
        CancellationToken cancellationToken)
    {
        var existe = await db.Campaigns
            .AsNoTracking()
            .AnyAsync(
                campaign => campaign.TituloNormalizado == tituloNormalizado &&
                            (ignorarId == null || campaign.Id != ignorarId),
                cancellationToken);

        if (existe)
        {
            throw new DuplicateCampaignTitleException(tituloOriginal);
        }
    }

    // 23505 = unique_violation no Postgres. Traduz a corrida perdida no indice unico para a mesma
    // excecao de dominio da pre-checagem, para o cliente ver 409 em vez de 500.
    private async Task SalvarTratandoTituloDuplicadoAsync(string titulo, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (EhTituloDuplicado(ex))
        {
            throw new DuplicateCampaignTitleException(titulo);
        }
    }

    private static bool EhTituloDuplicado(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres &&
        string.Equals(postgres.ConstraintName, "ix_campaigns_titulo_normalizado", StringComparison.Ordinal);

    public async Task<Campaign?> UpdateAsync(
        Guid id,
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        CampaignCategory categoria,
        CancellationToken cancellationToken,
        string? imagem = null)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (campaign is null)
        {
            return null;
        }

        // Ignora a propria campanha na checagem: renomear "Cestas" para "cestas" e valido.
        await GarantirTituloDisponivelAsync(
            Campaign.NormalizarTitulo(titulo), id, titulo, cancellationToken);

        campaign.Update(
            titulo,
            descricao,
            dataInicio,
            dataFim,
            metaFinanceira,
            status,
            DateTimeOffset.UtcNow,
            categoria,
            imagem);

        await SalvarTratandoTituloDuplicadoAsync(titulo, cancellationToken);
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
            0,
            campaign.Imagem);
    }

    // Mesma projecao a partir da entidade rastreada, usada no retorno da criacao em lote.
    private static CampanhaResponse ToResponse(Campaign campaign)
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
            0,
            campaign.Imagem);
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
