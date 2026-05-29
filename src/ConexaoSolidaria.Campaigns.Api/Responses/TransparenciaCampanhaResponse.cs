namespace ConexaoSolidaria.Campaigns.Api.Responses;

public sealed record TransparenciaCampanhaResponse(
    string Titulo,
    decimal MetaFinanceira,
    decimal ValorTotalArrecadado);
