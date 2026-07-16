using System.Text.Json.Serialization;
using ConexaoSolidaria.Shared.Domain;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Clients.Elasticsearch.Serialization;
using Elastic.Transport;

namespace ConexaoSolidaria.Campaigns.Api.Repositories;

public sealed record CampaignSearchDocument(
    Guid Id,
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    decimal ValorTotalArrecadado,
    CampaignStatus Status,
    // Default Outros cobre documentos antigos indexados antes desta coluna existir.
    CampaignCategory Categoria = CampaignCategory.Outros,
    // Rotulo humano da categoria ("Meio Ambiente", "Saude"...) indexado como texto analisado para
    // que a busca por termo tambem case a categoria por nome. Vazio em documentos antigos.
    string CategoriaTexto = "");

public sealed record CampaignSearchResult<T>(IReadOnlyCollection<T> Items, long Total);

public interface ICampaignSearchRepository
{
    /// <summary>
    /// Cria o indice com o mapeamento/analisadores pt-BR se ele ainda nao existir (idempotente).
    /// Retorna true quando o indice foi criado agora (sinal para disparar um backfill).
    /// </summary>
    Task<bool> EnsureIndexAsync(CancellationToken cancellationToken);

    Task IndexAsync(Campaign campaign, CancellationToken cancellationToken);

    /// <summary>Indexa varias campanhas em lote (usado no backfill inicial do indice).</summary>
    Task IndexManyAsync(IReadOnlyCollection<Campaign> campaigns, CancellationToken cancellationToken);

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

        // Serializa os enums como STRING no _source (ex.: status="Ativa", categoria="Saude") em vez de
        // numeros, permitindo mapea-los como keyword (filtro/exato) e le-los de volta corretamente.
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri(url)),
                sourceSerializer: (_, s) => new DefaultSourceSerializer(s, ConfigureSourceJson))
            .DefaultIndex(indexName);

        client = new ElasticsearchClient(settings);
    }

    private static void ConfigureSourceJson(System.Text.Json.JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<bool> EnsureIndexAsync(CancellationToken cancellationToken)
    {
        var exists = await client.Indices.ExistsAsync(indexName, cancellationToken);
        if (exists.Exists)
        {
            return false;
        }

        // O DSL fortemente tipado para analysis/filters e verboso e fragil entre versoes; enviamos a
        // definicao do indice como JSON cru pelo transporte de baixo nivel (mais robusto e legivel).
        var path = new EndpointPath(Elastic.Transport.HttpMethod.PUT, indexName);
        var response = await client.Transport.RequestAsync<StringResponse>(
            path,
            PostData.String(IndexDefinitionJson),
            null,
            null,
            cancellationToken);

        if (response.ApiCallDetails.HasSuccessfulStatusCode)
        {
            logger.LogInformation("Indice '{Index}' criado no Elasticsearch com analisadores pt-BR.", indexName);
            return true;
        }

        // Corrida entre replicas/processos: outro ja criou o indice. Nao e erro.
        if (response.ApiCallDetails.HttpStatusCode == StatusCodes.Status400BadRequest &&
            (response.Body?.Contains("resource_already_exists_exception", StringComparison.Ordinal) ?? false))
        {
            return false;
        }

        logger.LogWarning(
            "Falha ao criar o indice '{Index}' no Elasticsearch (HTTP {Status}). Corpo: {Body}",
            indexName,
            response.ApiCallDetails.HttpStatusCode,
            response.Body);

        return false;
    }

    public async Task IndexAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);

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

    public async Task IndexManyAsync(IReadOnlyCollection<Campaign> campaigns, CancellationToken cancellationToken)
    {
        if (campaigns.Count == 0)
        {
            return;
        }

        var documents = campaigns.Select(ToDocument).ToList();

        var response = await client.BulkAsync(
            bulk => bulk
                .Index(indexName)
                .IndexMany(documents, (operation, document) => operation.Id(document.Id)),
            cancellationToken);

        if (!response.IsValidResponse || response.Errors)
        {
            logger.LogWarning(
                "Backfill do Elasticsearch concluiu com falhas. Erro: {Error}",
                response.ElasticsearchServerError?.Error?.Reason ?? response.DebugInformation);
            return;
        }

        logger.LogInformation("Backfill do Elasticsearch indexou {Count} campanha(s).", documents.Count);
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

        var response = await client.SearchAsync<CampaignSearchDocument>(
            request => request
                .Indices(indexName)
                .From(from)
                .Size(pageSize)
                // Total exato (sem o teto padrao de 10.000) para a paginacao ficar correta.
                .TrackTotalHits(new TrackHits(true))
                .Query(BuildQuery(term))
                .Sort(sort => sort
                    .Score(new ScoreSort { Order = SortOrder.Desc })),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            // Indice ainda nao criado (ex.: nenhuma campanha indexada): trata como "sem resultados".
            if (response.ApiCallDetails.HttpStatusCode == StatusCodes.Status404NotFound)
            {
                return new CampaignSearchResult<CampaignSearchDocument>([], 0);
            }

            logger.LogError(
                "Falha ao buscar campanhas no Elasticsearch. Termo: {Term}. Erro: {Error}",
                term,
                response.ElasticsearchServerError?.Error?.Reason ?? response.DebugInformation);

            throw new InvalidOperationException("Falha ao buscar campanhas no Elasticsearch.");
        }

        return new CampaignSearchResult<CampaignSearchDocument>(
            response.Documents.ToList(),
            response.Total);
    }

    /// <summary>
    /// Busca avancada e tolerante a erros de digitacao sobre titulo, descricao e categoria.
    /// Combina, num bool/should (basta 1 casar), quatro estrategias complementares:
    /// <list type="number">
    /// <item>multi_match best_fields com fuzziness AUTO (Damerau-Levenshtein): absorve typos como
    /// "eduacao" -> "educacao". Os acentos ja sao normalizados pelo analisador (asciifolding).</item>
    /// <item>multi_match phrase_prefix: busca "conforme voce digita" (prefixo da ultima palavra).</item>
    /// <item>match no campo edge-ngram do titulo: autocomplete forte por prefixo de palavra.</item>
    /// <item>match_phrase no titulo com boost alto: frase exata no titulo sobe no ranking.</item>
    /// </list>
    /// Titulo pesa mais que descricao; a categoria entra como sinal adicional.
    /// </summary>
    private static Action<QueryDescriptor<CampaignSearchDocument>> BuildQuery(string term)
    {
        return query => query.Bool(boolQuery => boolQuery
            .Should(
                should => should.MultiMatch(multiMatch => multiMatch
                    .Query(term)
                    .Fields(new[] { "titulo^3", "descricao", "categoriaTexto^2" })
                    .Type(TextQueryType.BestFields)
                    .Fuzziness(new Fuzziness("AUTO"))
                    .PrefixLength(1)
                    .MaxExpansions(50)
                    .Operator(Operator.Or)),
                should => should.MultiMatch(multiMatch => multiMatch
                    .Query(term)
                    .Fields(new[] { "titulo^2", "descricao" })
                    .Type(TextQueryType.PhrasePrefix)),
                should => should.Match(match => match
                    .Field("titulo.prefix")
                    .Query(term)
                    .Boost(2)),
                should => should.MatchPhrase(matchPhrase => matchPhrase
                    .Field("titulo")
                    .Query(term)
                    .Boost(5)))
            .MinimumShouldMatch(1));
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
            campaign.Status,
            campaign.Categoria,
            CategoriaLabel(campaign.Categoria));
    }

    // Rotulo humano em pt-BR para a categoria, indexado como texto pesquisavel. Os acentos sao
    // removidos no indice pelo asciifolding, entao "saude"/"saúde" e "meio ambiente" casam igual.
    private static string CategoriaLabel(CampaignCategory categoria) => categoria switch
    {
        CampaignCategory.Saude => "Saude",
        CampaignCategory.Educacao => "Educacao",
        CampaignCategory.Alimentacao => "Alimentacao",
        CampaignCategory.Moradia => "Moradia",
        CampaignCategory.MeioAmbiente => "Meio Ambiente",
        CampaignCategory.Assistencia => "Assistencia",
        CampaignCategory.Animais => "Animais",
        CampaignCategory.Cultura => "Cultura",
        _ => "Outros"
    };

    // Definicao do indice: analisador pt-BR (minusculas + asciifolding para ignorar acentos +
    // stopwords + stemmer leve de portugues) e um campo edge-ngram no titulo para autocomplete.
    private const string IndexDefinitionJson = """
    {
      "settings": {
        "index": { "max_ngram_diff": 18 },
        "analysis": {
          "filter": {
            "pt_stop": { "type": "stop", "stopwords": "_portuguese_" },
            "pt_stemmer": { "type": "stemmer", "language": "light_portuguese" },
            "pt_ascii": { "type": "asciifolding", "preserve_original": true },
            "titulo_edge_ngram": { "type": "edge_ngram", "min_gram": 2, "max_gram": 20 }
          },
          "analyzer": {
            "campanha_analyzer": {
              "type": "custom",
              "tokenizer": "standard",
              "filter": ["lowercase", "pt_ascii", "pt_stop", "pt_stemmer"]
            },
            "campanha_prefix_index": {
              "type": "custom",
              "tokenizer": "standard",
              "filter": ["lowercase", "pt_ascii", "titulo_edge_ngram"]
            },
            "campanha_prefix_search": {
              "type": "custom",
              "tokenizer": "standard",
              "filter": ["lowercase", "pt_ascii"]
            }
          }
        }
      },
      "mappings": {
        "properties": {
          "id": { "type": "keyword" },
          "titulo": {
            "type": "text",
            "analyzer": "campanha_analyzer",
            "fields": {
              "prefix": {
                "type": "text",
                "analyzer": "campanha_prefix_index",
                "search_analyzer": "campanha_prefix_search"
              },
              "keyword": { "type": "keyword", "ignore_above": 256 }
            }
          },
          "descricao": { "type": "text", "analyzer": "campanha_analyzer" },
          "categoriaTexto": { "type": "text", "analyzer": "campanha_analyzer" },
          "categoria": { "type": "keyword" },
          "status": { "type": "keyword" },
          "dataInicio": { "type": "date" },
          "dataFim": { "type": "date" },
          "metaFinanceira": { "type": "double" },
          "valorTotalArrecadado": { "type": "double" }
        }
      }
    }
    """;
}
