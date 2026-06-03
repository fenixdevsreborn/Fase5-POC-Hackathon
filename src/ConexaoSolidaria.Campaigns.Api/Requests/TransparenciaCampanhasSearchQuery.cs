namespace ConexaoSolidaria.Campaigns.Api.Requests;

public sealed class TransparenciaCampanhasSearchQuery
{
    public const int DefaultPage = 1;

    public const int DefaultPageSize = 10;

    public const int MaxPageSize = TransparenciaCampanhasQuery.MaxPageSize;

    public string? Titulo { get; init; }

    public int Page { get; init; } = DefaultPage;

    public int PageSize { get; init; } = DefaultPageSize;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(Titulo))
        {
            errors[nameof(Titulo)] = ["Titulo e obrigatorio para busca."];
        }

        if (Page <= 0)
        {
            errors[nameof(Page)] = ["Page deve ser maior que zero."];
        }

        if (PageSize <= 0 || PageSize > MaxPageSize)
        {
            errors[nameof(PageSize)] = [$"PageSize deve estar entre 1 e {MaxPageSize}."];
        }

        return errors;
    }
}
