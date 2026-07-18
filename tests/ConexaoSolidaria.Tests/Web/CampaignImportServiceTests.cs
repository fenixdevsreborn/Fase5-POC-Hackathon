using System.Text;
using ConexaoSolidaria.Web.Services.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConexaoSolidaria.Tests.Web;

/// <summary>
/// Parsing da planilha de importacao. A tolerancia do formato (separador, cultura, cabecalho)
/// e a parte mais frageis do fluxo, entao fica coberta por teste em vez de so por inspecao.
/// </summary>
public sealed class CampaignImportServiceTests
{
    private static CampaignImportService Servico() =>
        new(NullLogger<CampaignImportService>.Instance);

    private static Stream Csv(string conteudo) =>
        new MemoryStream(Encoding.UTF8.GetBytes(conteudo));

    [Fact]
    public async Task LerAsync_DeveImportarCsvComPontoEVirgulaEValoresPtBr()
    {
        const string csv = """
            Titulo;Descricao;Categoria;MetaFinanceira;DataInicio;DataFim;Status
            Cestas basicas;Arrecadacao de cestas;Alimentacao;R$ 1.500,50;01/12/2026;31/12/2026;Ativa
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        var campanha = Assert.Single(resultado.Campanhas);
        Assert.Empty(resultado.Erros);
        Assert.Equal("Cestas basicas", campanha.Titulo);
        Assert.Equal("Alimentacao", campanha.Categoria);
        Assert.Equal(1500.50m, campanha.MetaFinanceira);
        Assert.Equal(new DateTime(2026, 12, 1), campanha.DataInicio.Date);
        Assert.Equal(new DateTime(2026, 12, 31), campanha.DataFim.Date);
        Assert.Equal("Ativa", campanha.Status);
    }

    [Fact]
    public async Task LerAsync_DeveImportarCsvComVirgulaEValoresInvariantes()
    {
        const string csv = """
            Titulo,Descricao,Categoria,MetaFinanceira,DataInicio,DataFim,Status
            Agasalhos,Campanha do agasalho,Assistencia,2500.75,2026-06-01,2026-07-01,Ativa
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        var campanha = Assert.Single(resultado.Campanhas);
        Assert.Equal(2500.75m, campanha.MetaFinanceira);
        Assert.Equal("Assistencia", campanha.Categoria);
    }

    // O separador decimal e deduzido da string, nao da cultura: tentar pt-BR e depois invariante
    // lia "2500.75" como 250075 (ponto como milhar) sem nunca chegar ao fallback.
    [Theory]
    [InlineData("1500", 1500)]
    [InlineData("1500,50", 1500.50)]
    [InlineData("2500.75", 2500.75)]
    [InlineData("R$ 1.500,50", 1500.50)]
    [InlineData("1.234.567,89", 1234567.89)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.500", 1500)]          // ponto unico + 3 digitos = milhar pt-BR
    [InlineData("R$1500", 1500)]
    public async Task LerAsync_DeveInterpretarOsFormatosDeValorUsuais(string entrada, decimal esperado)
    {
        var csv = $"""
            Titulo;Descricao;Meta
            Teste;Descricao ok;{entrada}
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        Assert.Equal(esperado, Assert.Single(resultado.Campanhas).MetaFinanceira);
    }

    // Cabecalho real vem com acento, caixa e espaco variados; o mapeamento normaliza antes de casar.
    [Fact]
    public async Task LerAsync_DeveCasarCabecalhoIgnorandoAcentoECaixa()
    {
        const string csv = """
            TÍTULO;DESCRIÇÃO;Meta Financeira
            Livros para todos;Doacao de livros;900
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        var campanha = Assert.Single(resultado.Campanhas);
        Assert.Equal("Livros para todos", campanha.Titulo);
        Assert.Equal(900m, campanha.MetaFinanceira);
    }

    // Descricao com o proprio separador dentro precisa vir entre aspas e sobreviver ao parse.
    [Fact]
    public async Task LerAsync_DeveRespeitarCamposEntreAspas()
    {
        const string csv = """
            Titulo;Descricao;Meta
            Reforma;"Telhado; piso e pintura da creche";5000
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        var campanha = Assert.Single(resultado.Campanhas);
        Assert.Equal("Telhado; piso e pintura da creche", campanha.Descricao);
    }

    [Fact]
    public async Task LerAsync_DeveReportarLinhaInvalidaSemDescartarAsValidas()
    {
        const string csv = """
            Titulo;Descricao;Meta
            Valida;Descricao ok;1000
            ;Sem titulo;500
            Meta zerada;Descricao ok;0
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        Assert.Single(resultado.Campanhas);
        Assert.Equal("Valida", resultado.Campanhas[0].Titulo);
        Assert.Equal(2, resultado.Erros.Count);
    }

    [Fact]
    public async Task LerAsync_DeveRejeitarDataFimAnteriorAInicio()
    {
        const string csv = """
            Titulo;Descricao;Meta;DataInicio;DataFim
            Invertida;Descricao ok;1000;31/12/2026;01/12/2026
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        Assert.Empty(resultado.Campanhas);
        Assert.Contains("término", Assert.Single(resultado.Erros).Motivo);
    }

    // Sem datas na planilha, o import assume o mesmo default do formulario manual (hoje / +30d).
    [Fact]
    public async Task LerAsync_SemDatas_DeveAplicarOsDefaultsDoFormulario()
    {
        const string csv = """
            Titulo;Descricao;Meta
            Sem datas;Descricao ok;1000
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        var campanha = Assert.Single(resultado.Campanhas);
        Assert.Equal(DateTime.Today, campanha.DataInicio.Date);
        Assert.Equal(DateTime.Today.AddDays(30), campanha.DataFim.Date);
    }

    [Fact]
    public async Task LerAsync_CategoriaDesconhecida_DeveCairEmOutros()
    {
        const string csv = """
            Titulo;Descricao;Meta;Categoria
            Teste;Descricao ok;1000;Categoria Inexistente
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        Assert.Equal("Outros", Assert.Single(resultado.Campanhas).Categoria);
    }

    [Fact]
    public async Task LerAsync_CategoriaPeloRotuloAcentuado_DeveCasarComOValorDoEnum()
    {
        const string csv = """
            Titulo;Descricao;Meta;Categoria
            Teste;Descricao ok;1000;Meio Ambiente
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        Assert.Equal("MeioAmbiente", Assert.Single(resultado.Campanhas).Categoria);
    }

    [Fact]
    public async Task LerAsync_SemColunaTitulo_DeveFalharComMensagemClara()
    {
        const string csv = """
            Coluna;Outra
            valor;outro
            """;

        var resultado = await Servico().LerAsync(Csv(csv), "campanhas.csv");

        Assert.Empty(resultado.Campanhas);
        Assert.Contains("Titulo", Assert.Single(resultado.Erros).Motivo);
    }

    [Fact]
    public async Task LerAsync_ArquivoVazio_NaoDeveLancar()
    {
        var resultado = await Servico().LerAsync(Csv(string.Empty), "campanhas.csv");

        Assert.Empty(resultado.Campanhas);
        Assert.NotEmpty(resultado.Erros);
    }

    // Arquivo que nao e um xlsx valido cai no catch e vira erro amigavel, nao excecao.
    [Fact]
    public async Task LerAsync_ExcelCorrompido_DeveDevolverErroAmigavel()
    {
        var lixo = new MemoryStream([0x01, 0x02, 0x03, 0x04, 0x05]);

        var resultado = await Servico().LerAsync(lixo, "campanhas.xlsx");

        Assert.Empty(resultado.Campanhas);
        Assert.NotEmpty(resultado.Erros);
    }

    // O modelo gerado precisa ser legivel pelo proprio parser, senao o botao "Baixar modelo"
    // entrega um arquivo que a importacao rejeita.
    [Fact]
    public async Task ModeloGerado_DeveSerLidoPeloProprioParser()
    {
        var modelo = new MemoryStream(CampaignImportService.GerarModeloExcel());

        var resultado = await Servico().LerAsync(modelo, "modelo-campanhas.xlsx");

        Assert.Empty(resultado.Erros);
        var campanha = Assert.Single(resultado.Campanhas);
        Assert.Equal("Alimentacao", campanha.Categoria);
        Assert.Equal(15000m, campanha.MetaFinanceira);
    }
}
