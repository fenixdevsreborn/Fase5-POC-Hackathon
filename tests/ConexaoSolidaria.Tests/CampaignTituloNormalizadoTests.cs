using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.Tests;

/// <summary>
/// Cobre a chave de unicidade de titulo. E ela que sustenta o indice UNICO
/// ix_campaigns_titulo_normalizado, entao o criterio de colisao precisa estar travado por teste.
/// </summary>
public sealed class CampaignTituloNormalizadoTests
{
    [Theory]
    [InlineData("Cestas de Natal", "cestas de natal")]
    [InlineData("  Cestas de Natal  ", "cestas de natal")]
    [InlineData("CESTAS DE NATAL", "cestas de natal")]
    [InlineData("Cestas   de    Natal", "cestas de natal")]
    [InlineData("Cestas\tde\nNatal", "cestas de natal")]
    public void NormalizarTitulo_DeveColapsarCaixaEEspacos(string entrada, string esperado)
    {
        Assert.Equal(esperado, Campaign.NormalizarTitulo(entrada));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizarTitulo_DeveDevolverVazioParaEntradaEmBranco(string? entrada)
    {
        Assert.Equal(string.Empty, Campaign.NormalizarTitulo(entrada!));
    }

    // Acento NAO e removido de proposito: "Solidário" e "Solidario" sao titulos diferentes para
    // o gestor. A normalizacao resolve caixa e espaco, nao equivalencia linguistica.
    [Fact]
    public void NormalizarTitulo_DevePreservarAcento()
    {
        Assert.NotEqual(
            Campaign.NormalizarTitulo("Natal Solidario"),
            Campaign.NormalizarTitulo("Natal Solidário"));
    }

    [Fact]
    public void Create_DeveGerarTituloNormalizadoAPartirDoTitulo()
    {
        var now = DateTimeOffset.UtcNow;

        var campanha = Campaign.Create(
            "  Cestas   de Natal ",
            "Arrecadacao para familias",
            now,
            now.AddDays(30),
            1000,
            CampaignStatus.Ativa,
            now);

        Assert.Equal("Cestas   de Natal", campanha.Titulo);
        Assert.Equal("cestas de natal", campanha.TituloNormalizado);
    }

    [Fact]
    public void Update_DeveRecalcularOTituloNormalizado()
    {
        var now = DateTimeOffset.UtcNow;
        var campanha = CriarCampanha(now, "Cestas de Natal");

        campanha.Update(
            "Cestas de Pascoa",
            "Nova descricao",
            now,
            now.AddDays(30),
            2000,
            CampaignStatus.Ativa,
            now);

        Assert.Equal("cestas de pascoa", campanha.TituloNormalizado);
    }

    [Fact]
    public void Create_SemImagem_DeveFicarNulo()
    {
        var campanha = CriarCampanha(DateTimeOffset.UtcNow, "Sem foto");

        Assert.Null(campanha.Imagem);
    }

    [Fact]
    public void Create_ComImagem_DeveGuardarONomeDoArquivo()
    {
        var now = DateTimeOffset.UtcNow;

        var campanha = Campaign.Create(
            "Com foto",
            "Descricao",
            now,
            now.AddDays(30),
            1000,
            CampaignStatus.Ativa,
            now,
            CampaignCategory.Outros,
            "3f2a9c1d4b5e6f708192a3b4c5d6e7f8.jpg");

        Assert.Equal("3f2a9c1d4b5e6f708192a3b4c5d6e7f8.jpg", campanha.Imagem);
    }

    // Contrato do PUT: null preserva a foto atual, string vazia remove. Sem isso, toda edicao
    // de titulo/meta apagaria a imagem da campanha.
    [Fact]
    public void Update_ComImagemNula_DevePreservarAImagemAtual()
    {
        var now = DateTimeOffset.UtcNow;
        var campanha = Campaign.Create(
            "Com foto", "Descricao", now, now.AddDays(30), 1000,
            CampaignStatus.Ativa, now, CampaignCategory.Outros, "abc.jpg");

        campanha.Update(
            "Com foto", "Outra descricao", now, now.AddDays(30), 2000,
            CampaignStatus.Ativa, now, CampaignCategory.Outros, imagem: null);

        Assert.Equal("abc.jpg", campanha.Imagem);
    }

    [Fact]
    public void Update_ComImagemVazia_DeveRemoverAImagem()
    {
        var now = DateTimeOffset.UtcNow;
        var campanha = Campaign.Create(
            "Com foto", "Descricao", now, now.AddDays(30), 1000,
            CampaignStatus.Ativa, now, CampaignCategory.Outros, "abc.jpg");

        campanha.Update(
            "Com foto", "Outra descricao", now, now.AddDays(30), 2000,
            CampaignStatus.Ativa, now, CampaignCategory.Outros, imagem: "");

        Assert.Null(campanha.Imagem);
    }

    private static Campaign CriarCampanha(DateTimeOffset now, string titulo) =>
        Campaign.Create(
            titulo,
            "Descricao da campanha",
            now,
            now.AddDays(30),
            1000,
            CampaignStatus.Ativa,
            now);
}
