using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using ConexaoSolidaria.Web.Components.Campaigns;

namespace ConexaoSolidaria.Web.Services.Import;

/// <summary>Linha da planilha que nao pode ser convertida, com o numero da linha para o gestor achar.</summary>
public sealed record ImportacaoErro(int Linha, string Motivo);

/// <summary>Resultado do parse: campanhas prontas para a lista + linhas rejeitadas.</summary>
public sealed record ImportacaoResultado(
    IReadOnlyList<SalvarCampanha> Campanhas,
    IReadOnlyList<ImportacaoErro> Erros)
{
    public static ImportacaoResultado Vazio { get; } =
        new(Array.Empty<SalvarCampanha>(), Array.Empty<ImportacaoErro>());
}

/// <summary>
/// Le uma planilha de campanhas (.xlsx via ClosedXML ou .csv) e devolve os itens para a tela de
/// lote REVISAR antes de salvar — a importacao nunca escreve direto no banco.
///
/// Tolerancia deliberada no parse, porque planilha real e bagunçada: cabecalho e casado sem
/// acento/caixa, datas aceitam dd/MM/yyyy e o serial numerico do Excel, e valores monetarios
/// aceitam "R$ 1.234,56" (pt-BR) e "1234.56" (invariante). Uma linha ruim vira erro daquela
/// linha, sem abortar o arquivo inteiro.
/// </summary>
public sealed class CampaignImportService(ILogger<CampaignImportService> logger)
{
    /// <summary>Teto por arquivo, alinhado ao limite do endpoint de lote da API.</summary>
    public const int MaximoLinhas = 200;

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    // Cabecalhos aceitos por coluna (comparados ja normalizados: sem acento, minusculo, sem espaco).
    private static readonly string[] ColunaTitulo = ["titulo", "nome", "campanha"];
    private static readonly string[] ColunaDescricao = ["descricao", "descrição", "detalhes"];
    private static readonly string[] ColunaMeta = ["meta", "metafinanceira", "valor", "valormeta"];
    private static readonly string[] ColunaInicio = ["datainicio", "inicio", "datadeinicio"];
    private static readonly string[] ColunaFim = ["datafim", "fim", "datadetermino", "termino"];
    private static readonly string[] ColunaCategoria = ["categoria", "area", "tema"];
    private static readonly string[] ColunaStatus = ["status", "situacao"];

    /// <summary>Cabecalho do arquivo modelo, na ordem em que e gerado.</summary>
    public static readonly string[] CabecalhoModelo =
        ["Titulo", "Descricao", "Categoria", "MetaFinanceira", "DataInicio", "DataFim", "Status"];

    public async Task<ImportacaoResultado> LerAsync(
        Stream conteudo,
        string nomeArquivo,
        CancellationToken cancellationToken = default)
    {
        // ClosedXML precisa de um stream seekable; o do browser nao e.
        using var buffer = new MemoryStream();
        await conteudo.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        if (buffer.Length == 0)
        {
            return new ImportacaoResultado(
                Array.Empty<SalvarCampanha>(),
                [new ImportacaoErro(0, "O arquivo está vazio.")]);
        }

        try
        {
            return nomeArquivo.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? LerCsv(buffer)
                : LerExcel(buffer);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao ler o arquivo de importacao {Arquivo}.", nomeArquivo);

            return new ImportacaoResultado(
                Array.Empty<SalvarCampanha>(),
                [new ImportacaoErro(0, "Não foi possível ler o arquivo. Confira se ele segue o modelo.")]);
        }
    }

    private ImportacaoResultado LerExcel(Stream conteudo)
    {
        using var workbook = new XLWorkbook(conteudo);
        var planilha = workbook.Worksheets.FirstOrDefault();
        if (planilha is null)
        {
            return new ImportacaoResultado(
                Array.Empty<SalvarCampanha>(),
                [new ImportacaoErro(0, "A planilha não tem nenhuma aba.")]);
        }

        var linhas = planilha.RangeUsed()?.RowsUsed().ToList();
        if (linhas is null || linhas.Count < 2)
        {
            return new ImportacaoResultado(
                Array.Empty<SalvarCampanha>(),
                [new ImportacaoErro(0, "A planilha não tem linhas de dados abaixo do cabeçalho.")]);
        }

        var cabecalho = linhas[0].Cells().Select(celula => celula.GetString()).ToList();
        var mapa = MapearColunas(cabecalho);
        if (mapa.Titulo < 0)
        {
            return new ImportacaoResultado(
                Array.Empty<SalvarCampanha>(),
                [new ImportacaoErro(1, "Não encontrei a coluna \"Titulo\" no cabeçalho.")]);
        }

        var campanhas = new List<SalvarCampanha>();
        var erros = new List<ImportacaoErro>();

        foreach (var linha in linhas.Skip(1))
        {
            if (campanhas.Count >= MaximoLinhas)
            {
                erros.Add(new ImportacaoErro(
                    linha.RowNumber(),
                    $"Importação limitada a {MaximoLinhas} campanhas por arquivo; as linhas seguintes foram ignoradas."));
                break;
            }

            // Celulas vindas do Excel: datas ja chegam tipadas quando a coluna esta formatada.
            var valores = linha.Cells(1, Math.Max(cabecalho.Count, 1))
                .Select(celula => celula.GetString())
                .ToList();

            var (campanha, erro) = Converter(valores, mapa, linha.RowNumber());
            if (campanha is not null)
            {
                campanhas.Add(campanha);
            }
            else if (erro is not null)
            {
                erros.Add(erro);
            }
        }

        return new ImportacaoResultado(campanhas, erros);
    }

    private ImportacaoResultado LerCsv(Stream conteudo)
    {
        using var leitor = new StreamReader(conteudo);
        var linhas = new List<string>();
        while (leitor.ReadLine() is { } linha)
        {
            linhas.Add(linha);
        }

        if (linhas.Count < 2)
        {
            return new ImportacaoResultado(
                Array.Empty<SalvarCampanha>(),
                [new ImportacaoErro(0, "O CSV não tem linhas de dados abaixo do cabeçalho.")]);
        }

        // Excel pt-BR salva CSV com ";" e Excel en-US com ","; decide pelo que aparece mais no cabecalho.
        var separador = linhas[0].Count(c => c == ';') >= linhas[0].Count(c => c == ',') ? ';' : ',';

        var mapa = MapearColunas(DividirCsv(linhas[0], separador));
        if (mapa.Titulo < 0)
        {
            return new ImportacaoResultado(
                Array.Empty<SalvarCampanha>(),
                [new ImportacaoErro(1, "Não encontrei a coluna \"Titulo\" no cabeçalho.")]);
        }

        var campanhas = new List<SalvarCampanha>();
        var erros = new List<ImportacaoErro>();

        for (var indice = 1; indice < linhas.Count; indice++)
        {
            var numeroLinha = indice + 1;

            if (string.IsNullOrWhiteSpace(linhas[indice]))
            {
                continue;
            }

            if (campanhas.Count >= MaximoLinhas)
            {
                erros.Add(new ImportacaoErro(
                    numeroLinha,
                    $"Importação limitada a {MaximoLinhas} campanhas por arquivo; as linhas seguintes foram ignoradas."));
                break;
            }

            var (campanha, erro) = Converter(DividirCsv(linhas[indice], separador), mapa, numeroLinha);
            if (campanha is not null)
            {
                campanhas.Add(campanha);
            }
            else if (erro is not null)
            {
                erros.Add(erro);
            }
        }

        return new ImportacaoResultado(campanhas, erros);
    }

    // CSV com suporte a campo entre aspas (descricao costuma ter virgula/ponto-e-virgula dentro).
    private static List<string> DividirCsv(string linha, char separador)
    {
        var campos = new List<string>();
        var atual = new StringBuilder();
        var dentroDeAspas = false;

        for (var i = 0; i < linha.Length; i++)
        {
            var caractere = linha[i];

            if (caractere == '"')
            {
                // "" dentro de um campo com aspas representa uma aspa literal.
                if (dentroDeAspas && i + 1 < linha.Length && linha[i + 1] == '"')
                {
                    atual.Append('"');
                    i++;
                }
                else
                {
                    dentroDeAspas = !dentroDeAspas;
                }
            }
            else if (caractere == separador && !dentroDeAspas)
            {
                campos.Add(atual.ToString());
                atual.Clear();
            }
            else
            {
                atual.Append(caractere);
            }
        }

        campos.Add(atual.ToString());
        return campos;
    }

    private sealed record MapaColunas(
        int Titulo,
        int Descricao,
        int Meta,
        int Inicio,
        int Fim,
        int Categoria,
        int Status);

    private static MapaColunas MapearColunas(IReadOnlyList<string> cabecalho)
    {
        var normalizado = cabecalho.Select(Normalizar).ToList();

        int Achar(string[] aceitos) => normalizado.FindIndex(coluna => aceitos.Contains(coluna));

        return new MapaColunas(
            Achar(ColunaTitulo),
            Achar(ColunaDescricao),
            Achar(ColunaMeta),
            Achar(ColunaInicio),
            Achar(ColunaFim),
            Achar(ColunaCategoria),
            Achar(ColunaStatus));
    }

    private static (SalvarCampanha? Campanha, ImportacaoErro? Erro) Converter(
        IReadOnlyList<string> valores,
        MapaColunas mapa,
        int numeroLinha)
    {
        var titulo = Celula(valores, mapa.Titulo);

        // Linha totalmente em branco no meio da planilha e ruido, nao erro.
        if (string.IsNullOrWhiteSpace(titulo) && valores.All(string.IsNullOrWhiteSpace))
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(titulo))
        {
            return (null, new ImportacaoErro(numeroLinha, "Título em branco."));
        }

        if (titulo.Length > 160)
        {
            titulo = titulo[..160];
        }

        var descricao = Celula(valores, mapa.Descricao);
        if (string.IsNullOrWhiteSpace(descricao))
        {
            return (null, new ImportacaoErro(numeroLinha, $"\"{titulo}\": descrição em branco."));
        }

        if (descricao.Length > 1000)
        {
            descricao = descricao[..1000];
        }

        var metaTexto = Celula(valores, mapa.Meta);
        if (!TentarLerDecimal(metaTexto, out var meta) || meta <= 0)
        {
            return (null, new ImportacaoErro(
                numeroLinha, $"\"{titulo}\": meta inválida (\"{metaTexto}\"). Use um número maior que zero."));
        }

        // Datas ausentes assumem hoje / +30 dias, mesmo default do formulário manual.
        var inicio = TentarLerData(Celula(valores, mapa.Inicio)) ?? DateTimeOffset.Now;
        var fim = TentarLerData(Celula(valores, mapa.Fim)) ?? inicio.AddDays(30);

        if (fim < inicio)
        {
            return (null, new ImportacaoErro(
                numeroLinha, $"\"{titulo}\": data de término anterior à de início."));
        }

        return (new SalvarCampanha(
            Titulo: titulo.Trim(),
            Descricao: descricao.Trim(),
            DataInicio: inicio,
            DataFim: fim,
            MetaFinanceira: meta,
            Status: NormalizarStatus(Celula(valores, mapa.Status)),
            Categoria: NormalizarCategoria(Celula(valores, mapa.Categoria))), null);
    }

    private static string Celula(IReadOnlyList<string> valores, int indice) =>
        indice >= 0 && indice < valores.Count ? valores[indice].Trim() : string.Empty;

    /// <summary>
    /// Aceita "R$ 1.234,56" (pt-BR) e "1234.56" (invariante). NAO da para tentar uma cultura e
    /// depois a outra: "2500.75" e um parse VALIDO em pt-BR e resulta em 250075 (ponto lido como
    /// separador de milhar). Entao o separador decimal e decidido pela propria string antes de
    /// converter, sempre pela cultura invariante no final.
    /// </summary>
    private static bool TentarLerDecimal(string texto, out decimal valor)
    {
        valor = 0;
        if (string.IsNullOrWhiteSpace(texto))
        {
            return false;
        }

        var limpo = texto
            .Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty)
            .Replace(" ", string.Empty) // espaco nao separavel, comum em copia/cola do Excel
            .Trim();

        if (limpo.Length == 0)
        {
            return false;
        }

        var ultimaVirgula = limpo.LastIndexOf(',');
        var ultimoPonto = limpo.LastIndexOf('.');

        char? separadorDecimal;
        if (ultimaVirgula >= 0 && ultimoPonto >= 0)
        {
            // Os dois presentes: o que aparece por ULTIMO e o decimal.
            // "1.234,56" -> ','   |   "1,234.56" -> '.'
            separadorDecimal = ultimaVirgula > ultimoPonto ? ',' : '.';
        }
        else if (ultimaVirgula >= 0)
        {
            // Virgula sozinha e sempre decimal no uso brasileiro ("1500,50").
            separadorDecimal = ',';
        }
        else if (ultimoPonto >= 0)
        {
            // Ponto sozinho e ambiguo. Um unico ponto seguido de exatamente 3 digitos e quase
            // sempre milhar pt-BR ("1.500" = mil e quinhentos); qualquer outro caso e decimal
            // ("2500.75"). Tradeoff assumido: "2.500" querendo dizer 2,5 seria lido como 2500 —
            // valor monetario com 3 casas nao aparece na pratica.
            var digitosDepois = limpo.Length - ultimoPonto - 1;
            var pontoUnico = limpo.IndexOf('.') == ultimoPonto;

            separadorDecimal = pontoUnico && digitosDepois == 3 ? null : '.';
        }
        else
        {
            separadorDecimal = null;
        }

        // Remove tudo que nao for digito, sinal ou o separador decimal escolhido, e normaliza
        // esse separador para "." para converter com a cultura invariante.
        var construtor = new StringBuilder(limpo.Length);
        foreach (var caractere in limpo)
        {
            if (char.IsDigit(caractere) || caractere == '-')
            {
                construtor.Append(caractere);
            }
            else if (separadorDecimal is not null && caractere == separadorDecimal)
            {
                construtor.Append('.');
            }
        }

        return decimal.TryParse(
            construtor.ToString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out valor);
    }

    private static DateTimeOffset? TentarLerData(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        if (DateTime.TryParse(texto, Br, DateTimeStyles.None, out var data) ||
            DateTime.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.None, out data))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(data.Date, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        // Célula de data não formatada vem como serial do Excel (dias desde 30/12/1899).
        if (double.TryParse(texto, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) &&
            serial is > 0 and < 100000)
        {
            var convertida = DateTime.FromOADate(serial).Date;
            return new DateTimeOffset(DateTime.SpecifyKind(convertida, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return null;
    }

    // Casa com os valores do seletor do formulário; qualquer coisa fora da lista vira "Outros".
    private static string NormalizarCategoria(string categoria)
    {
        if (string.IsNullOrWhiteSpace(categoria))
        {
            return "Outros";
        }

        var alvo = Normalizar(categoria);

        foreach (var (valor, rotulo) in CampaignVisuals.Categorias)
        {
            if (Normalizar(valor) == alvo || Normalizar(rotulo) == alvo)
            {
                return valor;
            }
        }

        return "Outros";
    }

    private static string NormalizarStatus(string status) => Normalizar(status) switch
    {
        "concluida" or "concluído" or "concluida." => "Concluida",
        "cancelada" => "Cancelada",
        _ => "Ativa"
    };

    /// <summary>
    /// Forma de comparacao para cabecalhos/categorias: sem acento, minusculo e sem espacos.
    /// Assim "Meta Financeira", "meta_financeira" e "METAFINANCEIRA" casam entre si.
    /// </summary>
    private static string Normalizar(string valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return string.Empty;
        }

        var decomposto = valor.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var construtor = new StringBuilder(decomposto.Length);

        foreach (var caractere in decomposto)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(caractere);
            if (categoria == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(caractere))
            {
                construtor.Append(caractere);
            }
        }

        return construtor.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Gera o arquivo .xlsx modelo, com o cabecalho esperado e uma linha de exemplo. Serve de
    /// contrato vivo: o gestor baixa, preenche e sobe de volta.
    /// </summary>
    public static byte[] GerarModeloExcel()
    {
        using var workbook = new XLWorkbook();
        var planilha = workbook.AddWorksheet("Campanhas");

        for (var coluna = 0; coluna < CabecalhoModelo.Length; coluna++)
        {
            var celula = planilha.Cell(1, coluna + 1);
            celula.Value = CabecalhoModelo[coluna];
            celula.Style.Font.Bold = true;
            celula.Style.Fill.BackgroundColor = XLColor.FromHtml("#0b3b60");
            celula.Style.Font.FontColor = XLColor.White;
        }

        planilha.Cell(2, 1).Value = "Cestas básicas para o Morro Azul";
        planilha.Cell(2, 2).Value = "Arrecadação de 200 cestas básicas para famílias em situação de "
                                    + "vulnerabilidade na comunidade do Morro Azul.";
        planilha.Cell(2, 3).Value = "Alimentacao";
        planilha.Cell(2, 4).Value = 15000;
        planilha.Cell(2, 5).Value = DateTime.Today.ToString("dd/MM/yyyy");
        planilha.Cell(2, 6).Value = DateTime.Today.AddDays(30).ToString("dd/MM/yyyy");
        planilha.Cell(2, 7).Value = "Ativa";

        planilha.Columns().AdjustToContents();
        planilha.Column(2).Width = 60;

        using var saida = new MemoryStream();
        workbook.SaveAs(saida);
        return saida.ToArray();
    }
}
