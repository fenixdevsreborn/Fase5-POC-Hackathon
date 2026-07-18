namespace ConexaoSolidaria.Campaigns.Api.Responses;

/// <summary>Campanha do lote que nao pode ser criada, com o motivo pronto para exibir na UI.</summary>
public sealed record CampanhaEmLoteErro(int Indice, string Titulo, string Motivo);

/// <summary>
/// Resultado da criacao em lote. Sucesso PARCIAL e o comportamento desejado: numa planilha de 50
/// linhas, uma linha com titulo repetido nao pode descartar as outras 49. A UI mantem na tela
/// apenas as campanhas que falharam, com o motivo, para o gestor corrigir e reenviar.
/// </summary>
public sealed record CriacaoEmLoteResponse(
    IReadOnlyList<CampanhaResponse> Criadas,
    IReadOnlyList<CampanhaEmLoteErro> Erros);
