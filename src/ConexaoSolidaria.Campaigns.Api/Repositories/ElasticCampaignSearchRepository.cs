using ConexaoSolidaria.Campaigns.Api.Domain;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace ConexaoSolidaria.Campaigns.Api.Repositories;

public sealed record CampaignSearchDocument(
    Guid Id,
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    decimal ValorTotalArrecadado,
    CampaignStatus Status);

public sealed record CampaignSearchResult<T>(IReadOnlyCollection<T> Items, long Total);

public interface ICampaignSearchRepository
{
    Task IndexAsync(Campaign campaign, CancellationToken cancellationToken);

    Task<CampaignSearchResult<CampaignSearchDocument>> SearchAsync(
        string term,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed class ElasticCampaignSearchRepository : ICampaignSearchRepository
{
    private readonly ElasticsearchClient client;
    private readonly ILogger<ElasticCampaignSearchRepository> logger;
    private readonly string indexName;

    public ElasticCampaignSearchRepository(
        IConfiguration configuration,
        ILogger<ElasticCampaignSearchRepository> logger)
    {
        var url = configuration["Elasticsearch:Url"] ?? "http://localhost:9200";
        indexName = configuration["Elasticsearch:IndexName"] ?? "campanhas";
        this.logger = logger;

        var settings = new ElasticsearchClientSettings(new Uri(url))
            .DefaultIndex(indexName);

        client = new ElasticsearchClient(settings);
    }

    public async Task IndexAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        var response = await client.IndexAsync(
            ToDocument(campaign),
            request => request
                .Index(indexName)
                .Id(campaign.Id),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            logger.LogError(
                "Falha ao indexar campanha {CampaignId} no Elasticsearch. Erro: {Error}",
                campaign.Id,
                response.ElasticsearchServerError?.Error?.Reason ?? response.DebugInformation);

            throw new InvalidOperationException("Falha ao indexar campanha no Elasticsearch.");
        }
    }

    public async Task<CampaignSearchResult<CampaignSearchDocument>> SearchAsync(
        string term,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return new CampaignSearchResult<CampaignSearchDocument>([], 0);
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var from = (page - 1) * pageSize;

        var countResponse = await client.CountAsync<CampaignSearchDocument>(
            request => request
                .Indices(indexName)
                .Query(query => query
                    .MultiMatch(multiMatch => multiMatch
                        .Query(term)
                        .Fields(new[] { "titulo^3", "descricao" })
                        .Fuzziness(new Fuzziness("AUTO")))),
            cancellationToken);

        if (!countResponse.IsValidResponse)
        {
            if (countResponse.ApiCallDetails.HttpStatusCode == StatusCodes.Status404NotFound)
            {
                return new CampaignSearchResult<CampaignSearchDocument>([], 0);
            }

            logger.LogError(
                "Falha ao contar campanhas no Elasticsearch. Termo: {Term}. Erro: {Error}",
                term,
                countResponse.ElasticsearchServerError?.Error?.Reason ?? countResponse.DebugInformation);

            throw new InvalidOperationException("Falha ao contar campanhas no Elasticsearch.");
        }

        var response = await client.SearchAsync<CampaignSearchDocument>(
            request => request
                .Indices(indexName)
                .From(from)
                .Size(pageSize)
                .Query(query => query
                    .MultiMatch(multiMatch => multiMatch
                        .Query(term)
                        .Fields(new[] { "titulo^3", "descricao" })
                        .Fuzziness(new Fuzziness("AUTO"))))
                .Sort(sort => sort
                    .Score(new ScoreSort { Order = SortOrder.Desc })),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            logger.LogError(
                "Falha ao buscar campanhas no Elasticsearch. Termo: {Term}. Erro: {Error}",
                term,
                response.ElasticsearchServerError?.Error?.Reason ?? response.DebugInformation);

            throw new InvalidOperationException("Falha ao buscar campanhas no Elasticsearch.");
        }

        return new CampaignSearchResult<CampaignSearchDocument>(
            response.Documents.ToList(),
            countResponse.Count);
    }

    private static CampaignSearchDocument ToDocument(Campaign campaign)
    {
        return new CampaignSearchDocument(
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
