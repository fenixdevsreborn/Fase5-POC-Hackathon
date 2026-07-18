using ConexaoSolidaria.Web.Services;

namespace ConexaoSolidaria.Web.Components.Campaigns;

/// <summary>
/// Recursos visuais de uma campanha. Quando o gestor enviou uma foto, ela e usada; sem foto
/// propria a imagem e escolhida de forma deterministica pelo Id, para a vitrine nunca ficar
/// vazia. A categoria vem do backend e aqui so mapeamos o valor do enum (ex.: "MeioAmbiente")
/// para um rotulo acentuado exibido no chip.
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

    /// <summary>
    /// Foto a exibir para a campanha: a imagem enviada pelo gestor quando existe, senao a
    /// ilustrativa deterministica por Id. Sobrecarga preferida — a de Id continua para os
    /// casos em que o chamador so tem o identificador.
    /// </summary>
    public static string ImagemDe(Guid id, string? imagem) =>
        ApiClient.UrlImagemCampanha(imagem) ?? ImagemDe(id);

    /// <summary>Caminho relativo (a partir de wwwroot) da foto ilustrativa da campanha.</summary>
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
