namespace ConexaoSolidaria.Campaigns.Api.Responses;

public sealed record PaginatedResponse<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    long Total,
    int TotalPages);
