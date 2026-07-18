namespace ConexaoSolidaria.Web.Services;

// DTOs compartilhados pela camada Web. Os nomes de propriedade casam com o JSON
// camelCase produzido pelas APIs (Identity / Campaigns) atras do Gateway.

/// <summary>Resposta de autenticacao (login e cadastro). Espelha AuthResponse da Identity.Api.</summary>
public sealed record AuthResult(
    Guid UsuarioId,
    string NomeCompleto,
    string Email,
    string Role,
    string AccessToken,
    DateTimeOffset ExpiraEm);

/// <summary>Campanha completa. Espelha CampanhaResponse da Campaigns.Api.</summary>
public sealed record CampanhaDto(
    Guid Id,
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    decimal ValorTotalArrecadado,
    string Status,
    string Categoria = "Outros",
    int TotalDoadores = 0,
    // Nome do arquivo de imagem enviado pelo gestor; null quando a campanha usa a foto
    // ilustrativa por categoria (ver CampaignVisuals).
    string? Imagem = null);

/// <summary>Item da vitrine publica de transparencia.</summary>
public sealed record TransparenciaDto(
    string Titulo,
    decimal MetaFinanceira,
    decimal ValorTotalArrecadado);

/// <summary>Read model de estatisticas por campanha. Espelha CampanhaStatsResponse da Campaigns.Api.</summary>
public sealed record CampanhaStatsDto(
    Guid CampaignId,
    string Titulo,
    decimal MetaFinanceira,
    decimal TotalArrecadado,
    long DoacoesProcessadas,
    DateTimeOffset AtualizadoEm);

/// <summary>Envelope de paginacao. Espelha PaginatedResponse da Campaigns.Api.</summary>
public sealed record Paginated<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total,
    int TotalPages)
{
    public static Paginated<T> Empty(int page = 1, int pageSize = 10) =>
        new(Array.Empty<T>(), page, pageSize, 0, 0);
}

/// <summary>Payload de criacao/atualizacao de campanha (gestor).</summary>
public sealed record SalvarCampanha(
    string Titulo,
    string Descricao,
    DateTimeOffset DataInicio,
    DateTimeOffset DataFim,
    decimal MetaFinanceira,
    string Status,
    string Categoria = "Outros",
    // Nome do arquivo devolvido por POST /api/campanhas/imagens. No PUT, null preserva a imagem
    // atual e string vazia remove.
    string? Imagem = null);

/// <summary>Imagem recem-enviada: nome do arquivo (vai no payload) e URL publica (preview).</summary>
public sealed record ImagemEnviada(string Arquivo, string Url);

/// <summary>Campanha do lote recusada pela API, com o motivo pronto para exibir.</summary>
public sealed record CampanhaLoteErro(int Indice, string Titulo, string Motivo);

/// <summary>
/// Resultado de POST /api/campanhas/lote. Sucesso parcial: o gestor ve quantas entraram e
/// mantem na tela apenas as que falharam, com o motivo.
/// </summary>
public sealed record CriacaoEmLoteResultado(
    IReadOnlyList<CampanhaDto> Criadas,
    IReadOnlyList<CampanhaLoteErro> Erros)
{
    public static CriacaoEmLoteResultado Vazio { get; } =
        new(Array.Empty<CampanhaDto>(), Array.Empty<CampanhaLoteErro>());
}

/// <summary>Payload de cadastro de doador.</summary>
public sealed record CadastroDoador(
    string NomeCompleto,
    string Email,
    string Cpf,
    string Senha);

/// <summary>Retorno 202 da criacao de doacao (processamento assincrono).</summary>
public sealed record DoacaoAceitaDto(
    Guid DoacaoId,
    Guid CampanhaId,
    decimal ValorDoacao,
    string Status,
    string Mensagem);

/// <summary>Status atual de uma doacao (polling).</summary>
public sealed record DoacaoStatusDto(
    Guid DoacaoId,
    Guid CampanhaId,
    decimal ValorDoacao,
    string Status,
    string CampanhaTitulo,
    DateTimeOffset CriadaEm,
    DateTimeOffset? ProcessadaEm);

/// <summary>Item da lista "Minhas doacoes" (doador). Espelha MinhaDoacaoResponse da Donations.Api.</summary>
public sealed record MinhaDoacaoDto(
    Guid DoacaoId,
    Guid CampanhaId,
    string CampanhaTitulo,
    decimal ValorDoacao,
    string Status,
    DateTimeOffset CriadaEm,
    DateTimeOffset? ProcessadaEm);
