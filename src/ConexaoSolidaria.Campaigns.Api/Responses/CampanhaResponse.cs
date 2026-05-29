using ConexaoSolidaria.Campaigns.Api.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Responses;

public sealed record CampanhaResponse(
    Guid Id,
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    decimal ValorTotalArrecadado,
    CampaignStatus Status);
