using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConexaoSolidaria.Campaigns.Api.Domain;
using Microsoft.Extensions.Options;

namespace ConexaoSolidaria.Campaigns.Api.Search;

public sealed class ElasticsearchCampaignSearchService(
    HttpClient httpClient,
    IOptions<ElasticsearchOptions> searchOptions,
    ILogger<ElasticsearchCampaignSearchService> logger) : ICampaignSearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ElasticsearchOptions options = searchOptions.Value;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        using var headResponse = await httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, IndexPath),
            cancellationToken);

        if (headResponse.StatusCode != HttpStatusCode.NotFound)
        {
            headResponse.EnsureSuccessStatusCode();
            return;
        }

        var createIndexRequest = new
        {
            settings = new
            {
                analysis = new
                {
                    analyzer = new Dictionary<string, object>
                    {
                        ["pt_br_folded"] = new
                        {
                            tokenizer = "standard",
                            filter = new[] { "lowercase", "asciifolding" }
                        }
                    }
                }
            },
            mappings = new
            {
                properties = new Dictionary<string, object>
                {
                    ["id"] = new { type = "keyword" },
                    ["titulo"] = new { type = "text", analyzer = "pt_br_folded" },
                    ["status"] = new { type = "keyword" },
                    ["dataFim"] = new { type = "date" }
                }
            }
        };

        using var createResponse = await httpClient.PutAsJsonAsync(
            IndexPath,
            createIndexRequest,
            JsonOptions,
            cancellationToken);

        if (createResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var responseBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            if (responseBody.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new HttpRequestException($"Falha ao criar indice no Elasticsearch: {responseBody}");
        }

        if (!createResponse.IsSuccessStatusCode)
        {
            createResponse.EnsureSuccessStatusCode();
        }
    }

    public async Task IndexAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await EnsureReadyAsync(cancellationToken);

            using var response = await httpClient.PutAsJsonAsync(
                $"{IndexPath}/_doc/{campaign.Id}?refresh=true",
                CampaignSearchDocument.From(campaign),
                JsonOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Nao foi possivel indexar a campanha {CampaignId} no Elasticsearch. StatusCode={StatusCode}.",
                    campaign.Id,
                    response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Elasticsearch indisponivel ao indexar a campanha {CampaignId}.", campaign.Id);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Timeout ao indexar a campanha {CampaignId} no Elasticsearch.", campaign.Id);
        }
    }

    public async Task<IReadOnlyCollection<Guid>?> SearchIdsByTitleAsync(
        string title,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var normalizedTitle = title.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return Array.Empty<Guid>();
        }

        try
        {
            await EnsureReadyAsync(cancellationToken);

            var searchRequest = new
            {
                size = Math.Clamp(options.MaxSearchResults, 1, 10000),
                _source = new[] { "id" },
                query = new
                {
                    match = new Dictionary<string, object>
                    {
                        ["titulo"] = new
                        {
                            query = normalizedTitle,
                            fuzziness = "AUTO",
                            prefix_length = 1,
                            @operator = "and"
                        }
                    }
                }
            };

            using var response = await httpClient.PostAsJsonAsync(
                $"{IndexPath}/_search",
                searchRequest,
                JsonOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Busca fuzzy de campanhas falhou no Elasticsearch. StatusCode={StatusCode}.",
                    response.StatusCode);
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var searchResponse = await JsonSerializer.DeserializeAsync<ElasticsearchSearchResponse>(
                responseStream,
                JsonOptions,
                cancellationToken);

            return searchResponse?.Hits?.Items
                .Select(item => item.Source?.Id)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray() ?? Array.Empty<Guid>();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Elasticsearch indisponivel para busca fuzzy por titulo.");
            return null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Timeout na busca fuzzy por titulo no Elasticsearch.");
            return null;
        }
    }

    private string IndexPath => options.IndexName.Trim('/');

    private sealed class ElasticsearchSearchResponse
    {
        [JsonPropertyName("hits")]
        public ElasticsearchHits? Hits { get; init; }
    }

    private sealed class ElasticsearchHits
    {
        [JsonPropertyName("hits")]
        public IReadOnlyCollection<ElasticsearchHit> Items { get; init; } = [];
    }

    private sealed class ElasticsearchHit
    {
        [JsonPropertyName("_source")]
        public CampaignSearchDocument? Source { get; init; }
    }
}
