namespace ConexaoSolidaria.Campaigns.Api.Responses;

/// <summary>
/// Retorno do upload de imagem: o nome gerado do arquivo (para mandar depois em
/// <c>SalvarCampanhaRequest.Imagem</c>) e a URL publica ja montada, que a Web usa no preview.
/// </summary>
public sealed record ImagemCampanhaResponse(string Arquivo, string Url);
