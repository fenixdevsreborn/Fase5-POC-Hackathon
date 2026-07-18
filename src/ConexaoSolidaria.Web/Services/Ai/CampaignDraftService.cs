using ConexaoSolidaria.Web.Components.Campaigns;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ConexaoSolidaria.Web.Services.Ai;

/// <summary>Resultado da geracao assistida: payload pronto para o form + justificativa da meta.</summary>
public sealed record CampaignDraftResult(SalvarCampanha Campanha, string Justificativa);

/// <summary>
/// Criacao assistida de campanhas (Feature: Gestor ONG). A partir de um rascunho/ideia
/// livre do gestor, o agente (sem tools) devolve um <see cref="CampaignDraftSuggestion"/>
/// via structured output, que e normalizado para os limites do formulario
/// (titulo &lt;= 160, descricao &lt;= 1000, meta &gt; 0, categoria valida do seletor).
/// Falhas retornam null — a pagina exibe Snackbar e o form permanece intacto.
/// </summary>
public sealed class CampaignDraftService(
    AiChatClientProvider provider,
    ILogger<CampaignDraftService> logger)
{
    private const string Instrucoes =
        """
        Voce ajuda gestores de ONGs da plataforma Conexao Solidaria a estruturar campanhas
        de doacao. A partir da ideia enviada, produza um rascunho completo em portugues do
        Brasil: titulo mobilizador, descricao persuasiva (causa, impacto e chamado a acao)
        e uma meta de arrecadacao realista em reais.
        """;

    private const string InstrucoesLote =
        """
        Voce ajuda gestores de ONGs da plataforma Conexao Solidaria a estruturar campanhas
        de doacao. A partir da ideia enviada, produza VARIAS campanhas complementares em
        portugues do Brasil, cada uma com titulo mobilizador, descricao persuasiva (causa,
        impacto e chamado a acao) e meta de arrecadacao realista em reais.

        Regras: cada campanha deve ter um TITULO UNICO e atacar um recorte diferente da causa
        (publico atendido, regiao ou tipo de ajuda). Nunca repita o mesmo titulo com pequenas
        variacoes, e nunca gere duas campanhas com o mesmo objetivo.
        """;

    public const int TituloMax = 160;
    public const int DescricaoMax = 1000;
    public const decimal MetaFallback = 5000m;

    /// <summary>Teto de campanhas por geracao — controla custo/latencia e o tamanho da lista na tela.</summary>
    public const int MaximoPorGeracao = 10;

    private AIAgent? _agent;
    private AIAgent? _agentLote;

    public bool Enabled => provider.Enabled;

    /// <summary>Gera o rascunho da campanha a partir da ideia do gestor; null em falha.</summary>
    public async Task<CampaignDraftResult?> GerarAsync(string ideia, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ideia))
        {
            return null;
        }

        try
        {
            _agent ??= provider.GetClient().AsAIAgent(new ChatClientAgentOptions
            {
                Name = "RascunhoDeCampanha",
                ChatOptions = new ChatOptions { Instructions = Instrucoes },
            });

            var resposta = await _agent.RunAsync<CampaignDraftSuggestion>(
                $"Ideia do gestor para a campanha: {ideia.Trim()}",
                cancellationToken: ct);

            return Normalizar(resposta.Result);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao gerar rascunho de campanha com IA.");
            return null;
        }
    }

    /// <summary>
    /// Gera VARIAS campanhas distintas a partir de uma unica ideia, para o gestor revisar e salvar
    /// em lote. Titulos repetidos entre si sao descartados aqui (o backend rejeitaria de qualquer
    /// forma, mas e melhor nunca colocar duplicata na tela). Lista vazia em falha.
    /// </summary>
    public async Task<IReadOnlyList<CampaignDraftResult>> GerarVariasAsync(
        string ideia,
        int quantidade,
        CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ideia))
        {
            return Array.Empty<CampaignDraftResult>();
        }

        var alvo = Math.Clamp(quantidade, 1, MaximoPorGeracao);

        try
        {
            _agentLote ??= provider.GetClient().AsAIAgent(new ChatClientAgentOptions
            {
                Name = "RascunhoDeCampanhasEmLote",
                ChatOptions = new ChatOptions { Instructions = InstrucoesLote },
            });

            var resposta = await _agentLote.RunAsync<CampaignDraftBatch>(
                $"Gere exatamente {alvo} campanhas distintas a partir desta ideia do gestor: {ideia.Trim()}",
                cancellationToken: ct);

            var sugestoes = resposta.Result?.Campanhas;
            if (sugestoes is null || sugestoes.Count == 0)
            {
                return Array.Empty<CampaignDraftResult>();
            }

            var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultados = new List<CampaignDraftResult>();

            foreach (var sugestao in sugestoes.Take(alvo))
            {
                var resultado = Normalizar(sugestao);

                // Titulo vazio ou repetido nao vira card: seria recusado no POST /lote.
                if (string.IsNullOrWhiteSpace(resultado.Campanha.Titulo) ||
                    !vistos.Add(resultado.Campanha.Titulo.Trim()))
                {
                    continue;
                }

                resultados.Add(resultado);
            }

            return resultados;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<CampaignDraftResult>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao gerar campanhas em lote com IA.");
            return Array.Empty<CampaignDraftResult>();
        }
    }

    /// <summary>
    /// Normaliza a sugestao do modelo para os limites/valores validos do formulario.
    /// Publico e puro para ser testavel sem chamadas de rede.
    /// </summary>
    public static CampaignDraftResult Normalizar(CampaignDraftSuggestion sugestao)
    {
        var titulo = Truncar(sugestao.Titulo?.Trim() ?? string.Empty, TituloMax);
        var descricao = Truncar(sugestao.Descricao?.Trim() ?? string.Empty, DescricaoMax);
        var meta = sugestao.MetaSugerida > 0 ? decimal.Round(sugestao.MetaSugerida, 2) : MetaFallback;
        var categoria = NormalizarCategoria(sugestao.Categoria);

        var campanha = new SalvarCampanha(
            Titulo: titulo,
            Descricao: descricao,
            DataInicio: DateTimeOffset.Now,
            DataFim: DateTimeOffset.Now.AddDays(30),
            MetaFinanceira: meta,
            Status: "Ativa",
            Categoria: categoria);

        return new CampaignDraftResult(campanha, sugestao.Justificativa?.Trim() ?? string.Empty);
    }

    /// <summary>Casa a categoria sugerida com os valores do seletor (case-insensitive); fallback "Outros".</summary>
    public static string NormalizarCategoria(string? categoria)
    {
        if (string.IsNullOrWhiteSpace(categoria))
        {
            return "Outros";
        }

        foreach (var (value, _) in CampaignVisuals.Categorias)
        {
            if (string.Equals(value, categoria.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return value; // casing canonico esperado pelo MudSelect e pelo backend
            }
        }

        return "Outros";
    }

    private static string Truncar(string valor, int max) =>
        valor.Length <= max ? valor : valor[..max];
}
