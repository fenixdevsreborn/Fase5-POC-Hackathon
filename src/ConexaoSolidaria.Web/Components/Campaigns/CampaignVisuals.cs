namespace ConexaoSolidaria.Web.Components.Campaigns;

/// <summary>
/// Recursos visuais de uma campanha. A imagem e escolhida de forma deterministica pelo Id
/// (o DTO nao carrega foto), enquanto a categoria agora vem do backend e aqui so mapeamos o
/// valor do enum (ex.: "MeioAmbiente") para um rotulo acentuado exibido no chip.
/// </summary>
public static class CampaignVisuals
{
    private static readonly string[] Imagens =
    {
        "images/campanhas/web/medium-shot-mother-girl-home.jpg",
        "images/campanhas/web/medium-shot-volunteers-with-donations.jpg",
        "images/campanhas/web/turns-out-exercise-can-be-fun-shot-group-kids-giving-each-other-high-five-summer-camp.jpg",
        "images/campanhas/web/view-little-boys-with-backpacks-spending-time-nature-outdoors.jpg",
        "images/campanhas/web/young-kids-exploring-together-nature.jpg",
        "images/campanhas/web/little-children-trick-treating-halloween.jpg",
    };

    // Valor do enum (como chega no JSON) -> rotulo acentuado para exibicao.
    private static readonly Dictionary<string, string> Rotulos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Saude"] = "Saúde",
        ["Educacao"] = "Educação",
        ["Alimentacao"] = "Alimentação",
        ["Moradia"] = "Moradia",
        ["MeioAmbiente"] = "Meio Ambiente",
        ["Assistencia"] = "Assistência",
        ["Animais"] = "Animais",
        ["Cultura"] = "Cultura",
        ["Outros"] = "Outros",
    };

    /// <summary>Categorias disponiveis para o seletor do formulario: (valor do enum, rotulo).</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Categorias = new[]
    {
        ("Saude", "Saúde"),
        ("Educacao", "Educação"),
        ("Alimentacao", "Alimentação"),
        ("Moradia", "Moradia"),
        ("MeioAmbiente", "Meio Ambiente"),
        ("Assistencia", "Assistência"),
        ("Animais", "Animais"),
        ("Cultura", "Cultura"),
        ("Outros", "Outros"),
    };

    /// <summary>Caminho relativo (a partir de wwwroot) da foto da campanha.</summary>
    public static string ImagemDe(Guid id)
    {
        var bytes = id.ToByteArray();
        var soma = 0;
        foreach (var b in bytes)
        {
            soma += b;
        }

        return Imagens[soma % Imagens.Length];
    }

    /// <summary>Rotulo acentuado da categoria (fallback: o proprio valor recebido).</summary>
    public static string RotuloCategoria(string? categoria)
    {
        if (string.IsNullOrWhiteSpace(categoria))
        {
            return "Outros";
        }

        return Rotulos.TryGetValue(categoria, out var rotulo) ? rotulo : categoria;
    }
}
