using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Requests;

public sealed record SalvarCampanhaRequest(
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    CampaignStatus Status,
    CampaignCategory Categoria = CampaignCategory.Outros);
