using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Responses;

public sealed record CampanhaResponse(
    Guid Id,
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    decimal ValorTotalArrecadado,
    CampaignStatus Status,
    CampaignCategory Categoria,
    // Numero de doacoes processadas (proxy de "doadores"). Vem do read model campaign_stats,
    // populado pelo Donations.Worker; 0 enquanto nao ha doacoes processadas.
    int TotalDoadores,
    // Nome do arquivo de imagem enviado pelo gestor; null quando a campanha nao tem foto propria
    // (a Web cai na imagem ilustrativa por categoria).
    string? Imagem = null);
