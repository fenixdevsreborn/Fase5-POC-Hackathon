using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConexaoSolidaria.Web.Services;

/// <summary>
/// Cliente HTTP tipado que fala com o Gateway (base address "http://gateway",
/// resolvido via service discovery do Aspire). Anexa o header
/// Authorization: Bearer {token} quando ha usuario logado.
///
/// Convencao de erro: em falha HTTP (status != sucesso) os metodos retornam null
/// (ou uma pagina vazia) para que as paginas exibam estado de erro/vazio.
/// </summary>
public sealed class ApiClient(HttpClient http, TokenProvider tokenProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ---------- Autenticacao ----------

    public Task<AuthResult?> LoginAsync(string email, string senha, CancellationToken ct = default) =>
        PostAsync<AuthResult>("api/auth/login", new { email, senha }, ct);

    public Task<AuthResult?> CadastrarDoadorAsync(CadastroDoador dto, CancellationToken ct = default) =>
        PostAsync<AuthResult>("api/auth/cadastro-doador", dto, ct);

    // ---------- Campanhas (publico + gestor) ----------

    /// <summary>
    /// Busca campanhas com tolerancia a erro de digitacao (Elasticsearch no servidor, com
    /// fallback para o PostgreSQL). Com <paramref name="apenasAtivas"/>, o servidor restringe a
    /// campanhas em andamento (Ativa + no prazo) — recorte usado pela vitrine de transparencia.
    /// Filtrar no servidor, e nao aqui, mantem Total/TotalPages coerentes com o que e exibido.
    /// </summary>
    public async Task<Paginated<CampanhaDto>> BuscarCampanhasAsync(
        string? q,
        int page,
        int pageSize,
        CancellationToken ct = default,
        bool apenasAtivas = false)
    {
        var url = $"api/campanhas/search?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(q))
        {
            url += $"&q={Uri.EscapeDataString(q)}";
        }

        if (apenasAtivas)
        {
            url += "&apenasAtivas=true";
        }

        var result = await GetAsync<Paginated<CampanhaDto>>(url, ct);
        return result ?? Paginated<CampanhaDto>.Empty(page, pageSize);
    }

    public Task<CampanhaDto?> ObterCampanhaAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<CampanhaDto>($"api/campanhas/{id}", ct);

    public async Task<IReadOnlyList<TransparenciaDto>> TransparenciaAsync(CancellationToken ct = default) =>
        await GetAsync<IReadOnlyList<TransparenciaDto>>("api/campanhas/transparencia", ct)
        ?? Array.Empty<TransparenciaDto>();

    public async Task<IReadOnlyList<CampanhaStatsDto>> StatsCampanhasAsync(CancellationToken ct = default) =>
        await GetAsync<IReadOnlyList<CampanhaStatsDto>>("api/campanhas/stats", ct)
        ?? Array.Empty<CampanhaStatsDto>();

    public Task<CampanhaDto?> CriarCampanhaAsync(SalvarCampanha dto, CancellationToken ct = default) =>
        PostAsync<CampanhaDto>("api/campanhas", dto, ct);

    public Task<CampanhaDto?> AtualizarCampanhaAsync(Guid id, SalvarCampanha dto, CancellationToken ct = default) =>
        PutAsync<CampanhaDto>($"api/campanhas/{id}", dto, ct);

    /// <summary>
    /// Cria varias campanhas de uma vez. A API responde 200 mesmo com falhas parciais, entao o
    /// retorno traz as criadas e as recusadas separadamente. Null so em falha de transporte.
    /// </summary>
    public Task<CriacaoEmLoteResultado?> CriarCampanhasEmLoteAsync(
        IReadOnlyList<SalvarCampanha> campanhas,
        CancellationToken ct = default) =>
        PostAsync<CriacaoEmLoteResultado>("api/campanhas/lote", new { campanhas }, ct);

    /// <summary>
    /// Envia a imagem antes da campanha existir e devolve o nome do arquivo para colocar em
    /// <see cref="SalvarCampanha.Imagem"/>. Null quando a API recusa (formato/tamanho) ou o
    /// transporte falha — a pagina mostra o aviso e mantem a campanha sem foto.
    /// </summary>
    public async Task<ImagemEnviada?> EnviarImagemCampanhaAsync(
        Stream conteudo,
        string nomeArquivo,
        string contentType,
        CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "api/campanhas/imagens");

            // MultipartFormDataContent assume a posse dos filhos; o using externo cobre os dois.
            using var form = new MultipartFormDataContent();
            var arquivo = new StreamContent(conteudo);
            arquivo.Headers.ContentType = new MediaTypeHeaderValue(contentType);

            // "arquivo" precisa casar com o nome do parametro IFormFile no controller.
            form.Add(arquivo, "arquivo", nomeArquivo);
            request.Content = form;

            using var response = await http.SendAsync(request, ct);
            return await ReadAsync<ImagemEnviada>(response, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// URL que o BROWSER usa para carregar a imagem. Aponta para uma rota da propria Web, nao para
    /// o Gateway: o Gateway nao e publicado para o navegador (so a Web e), entao o endpoint
    /// <c>/imagens/campanhas/{arquivo}</c> registrado no Program.cs faz o proxy server-side.
    /// Null quando a campanha nao tem foto propria — a UI cai na imagem por categoria.
    /// </summary>
    public static string? UrlImagemCampanha(string? arquivo) =>
        string.IsNullOrWhiteSpace(arquivo) ? null : $"/imagens/campanhas/{arquivo}";

    /// <summary>
    /// Busca os bytes da imagem no Gateway para o proxy da Web repassar ao browser.
    /// Null quando a imagem nao existe ou a API esta indisponivel.
    /// </summary>
    public async Task<(Stream Content, string ContentType)?> BaixarImagemCampanhaAsync(
        string arquivo,
        CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"api/campanhas/imagens/{arquivo}");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var content = await response.Content.ReadAsStreamAsync(ct);
            return (content, contentType);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // ---------- Doacoes (doador) ----------

    public Task<DoacaoAceitaDto?> DoarAsync(Guid idCampanha, decimal valor, CancellationToken ct = default) =>
        PostAsync<DoacaoAceitaDto>("api/doacoes", new { idCampanha, valorDoacao = valor }, ct);

    public Task<DoacaoStatusDto?> StatusDoacaoAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<DoacaoStatusDto>($"api/doacoes/{id}", ct);

    public async Task<IReadOnlyList<MinhaDoacaoDto>> MinhasDoacoesAsync(CancellationToken ct = default) =>
        await GetAsync<IReadOnlyList<MinhaDoacaoDto>>("api/doacoes/minhas", ct)
        ?? Array.Empty<MinhaDoacaoDto>();

    // ---------- Ciclo de vida de campanhas (gestor) ----------

    public Task<CampanhaDto?> AtivarCampanhaAsync(Guid id, CancellationToken ct = default) =>
        PostAsync<CampanhaDto>($"api/campanhas/{id}/ativar", new { }, ct);

    public Task<CampanhaDto?> ConcluirCampanhaAsync(Guid id, CancellationToken ct = default) =>
        PostAsync<CampanhaDto>($"api/campanhas/{id}/concluir", new { }, ct);

    public Task<CampanhaDto?> CancelarCampanhaAsync(Guid id, CancellationToken ct = default) =>
        PostAsync<CampanhaDto>($"api/campanhas/{id}/cancelar", new { }, ct);

    // ---------- Infra HTTP ----------

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, ct);
            return await ReadAsync<T>(response, ct);
        }
        catch (HttpRequestException)
        {
            return default;
        }
    }

    private async Task<T?> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, url);
            request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await http.SendAsync(request, ct);
            return await ReadAsync<T>(response, ct);
        }
        catch (HttpRequestException)
        {
            return default;
        }
    }

    private async Task<T?> PutAsync<T>(string url, object body, CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Put, url);
            request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await http.SendAsync(request, ct);
            return await ReadAsync<T>(response, ct);
        }
        catch (HttpRequestException)
        {
            return default;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (tokenProvider.HasToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenProvider.Token);
        }

        return request;
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
        {
            return default;
        }

        if (response.Content.Headers.ContentLength is 0)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }
}
