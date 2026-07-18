using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Requests;

public sealed record SalvarCampanhaRequest(
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    CampaignStatus Status,
    CampaignCategory Categoria = CampaignCategory.Outros,
    // Nome do arquivo devolvido por POST /api/campanhas/imagens. Null no PUT preserva a imagem
    // atual; string vazia remove. A imagem e enviada antes da campanha existir justamente para
    // que a tela de lote possa montar varias campanhas com foto antes de salvar qualquer uma.
    string? Imagem = null);
