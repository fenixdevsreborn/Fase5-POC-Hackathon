using System.ComponentModel;

namespace ConexaoSolidaria.Web.Services.Ai;

/// <summary>
/// Alvo do structured output da criacao assistida de campanhas: o agente devolve este
/// JSON tipado a partir do rascunho/ideia do gestor. A Categoria e string no schema
/// (valores permitidos na Description) e e validada depois contra o enum CampaignCategory
/// — com fallback "Outros" — para nao acoplar o schema ao enum do dominio.
/// </summary>
public sealed record CampaignDraftSuggestion(
    [property: Description("Titulo curto e mobilizador da campanha, com no maximo 160 caracteres.")]
    string Titulo,
    [property: Description("Descricao persuasiva em portugues do Brasil, 2 a 4 paragrafos, no maximo 1000 caracteres. Explique a causa, o impacto da doacao e um chamado a acao.")]
    string Descricao,
    [property: Description("Categoria da campanha. Exatamente um destes valores: Saude, Educacao, Alimentacao, Moradia, MeioAmbiente, Assistencia, Animais, Cultura, Outros.")]
    string Categoria,
    [property: Description("Meta de arrecadacao sugerida em reais (BRL), numero positivo realista para o tamanho da causa descrita.")]
    decimal MetaSugerida,
    [property: Description("Justificativa de uma frase para a meta sugerida.")]
    string Justificativa);

/// <summary>
/// Alvo do structured output quando o gestor pede VARIAS campanhas de uma vez. Um record
/// envelope (em vez de devolver um array na raiz) porque o schema de structured output da
/// OpenAI exige um objeto no topo.
/// </summary>
public sealed record CampaignDraftBatch(
    [property: Description(
        "Lista de campanhas distintas geradas a partir da ideia. Cada uma deve ter um titulo " +
        "UNICO e um recorte proprio da causa (publico, regiao ou tipo de ajuda diferente) — " +
        "nunca variacoes do mesmo titulo.")]
    IReadOnlyList<CampaignDraftSuggestion> Campanhas);
