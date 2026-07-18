namespace ConexaoSolidaria.Campaigns.Api.Requests;

/// <summary>
/// Criacao de varias campanhas numa unica chamada. Atende tanto a tela de lote do gestor
/// (adicionar N campanhas antes de salvar) quanto a importacao de planilha.
/// </summary>
public sealed record SalvarCampanhasEmLoteRequest(IReadOnlyList<SalvarCampanhaRequest> Campanhas);
