namespace ConexaoSolidaria.Campaigns.Api.Search;

public sealed class ElasticsearchOptions
{
    public const string SectionName = "Elasticsearch";

    public bool Enabled { get; init; } = true;

    public string Url { get; init; } = "http://localhost:9200";

    public string IndexName { get; init; } = "conexao-solidaria-campanhas";

    public int MaxSearchResults { get; init; } = 1000;
}
