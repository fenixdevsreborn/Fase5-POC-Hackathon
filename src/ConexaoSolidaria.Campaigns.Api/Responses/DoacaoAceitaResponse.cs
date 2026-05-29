namespace ConexaoSolidaria.Campaigns.Api.Responses;

public sealed record DoacaoAceitaResponse(
    Guid DoacaoId,
    Guid CampanhaId,
    decimal ValorDoacao,
    string Status,
    string Mensagem);
