using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Services;

/// <summary>Configuracao do storage de imagens de campanha (secao "CampaignImages").</summary>
public sealed class CampaignImageOptions
{
    public const string SectionName = "CampaignImages";

    /// <summary>
    /// Diretorio onde os arquivos sao gravados. Em container o pod monta um volume aqui
    /// (o rootfs e read-only); em dev cai numa pasta sob o diretorio da aplicacao.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Tamanho maximo aceito por arquivo, em bytes (default 5 MB).</summary>
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;
}

/// <summary>Arquivo pronto para ser devolvido no GET da imagem.</summary>
public sealed record CampaignImageFile(Stream Content, string ContentType);

public interface ICampaignImageStorage
{
    /// <summary>
    /// Persiste a imagem enviada e devolve o NOME gerado do arquivo (nunca o nome original).
    /// Lanca <see cref="DomainRuleException"/> (=&gt; 422) quando o arquivo e vazio, excede
    /// <see cref="CampaignImageOptions.MaxBytes"/> ou nao e um JPEG/PNG/WebP real.
    /// </summary>
    Task<string> SalvarAsync(Stream conteudo, string nomeOriginal, CancellationToken cancellationToken);

    /// <summary>Abre a imagem para leitura; null quando o arquivo nao existe.</summary>
    CampaignImageFile? Abrir(string arquivo);
}

/// <summary>
/// Storage de imagens em disco. Escolha consciente para esta POC: a Campaigns.Api roda com
/// <c>replicas: 1</c> e um volume dedicado montado em <see cref="CampaignImageOptions.RootPath"/>
/// (ver infra/k8s/base/campaigns-api.yaml). Escalar a API para mais de uma replica exige trocar o
/// PVC para ReadWriteMany ou migrar para um object storage — o resto do codigo nao muda, so esta classe.
///
/// Seguranca: o nome do arquivo e sempre gerado aqui (Guid + extensao canonica), o nome enviado pelo
/// usuario e usado apenas para descobrir a extensao pretendida, e a leitura valida o nome antes de
/// tocar o disco. Nenhum caminho vindo do cliente chega ao filesystem.
/// </summary>
public sealed class CampaignImageStorage : ICampaignImageStorage
{
    // Assinaturas (magic bytes) dos formatos aceitos. Confiar na extensao ou no Content-Type
    // enviado pelo browser permitiria subir um arquivo arbitrario com nome ".jpg".
    private static readonly string[] ExtensoesValidas = [".jpg", ".jpeg", ".png", ".webp"];

    private readonly string rootPath;
    private readonly long maxBytes;
    private readonly ILogger<CampaignImageStorage> logger;

    public CampaignImageStorage(CampaignImageOptions options, ILogger<CampaignImageStorage> logger)
    {
        this.logger = logger;
        maxBytes = options.MaxBytes > 0 ? options.MaxBytes : 5 * 1024 * 1024;

        rootPath = string.IsNullOrWhiteSpace(options.RootPath)
            ? Path.Combine(AppContext.BaseDirectory, "uploads", "campanhas")
            : Path.GetFullPath(options.RootPath);

        Directory.CreateDirectory(rootPath);
        logger.LogInformation("Imagens de campanha serao gravadas em {RootPath}.", rootPath);
    }

    public async Task<string> SalvarAsync(
        Stream conteudo,
        string nomeOriginal,
        CancellationToken cancellationToken)
    {
        // Copia para memoria primeiro: precisamos inspecionar os primeiros bytes (formato real) e
        // conhecer o tamanho antes de escrever qualquer coisa no disco. O limite e pequeno (5 MB).
        using var buffer = new MemoryStream();
        await CopiarComLimiteAsync(conteudo, buffer, cancellationToken);

        if (buffer.Length == 0)
        {
            throw new DomainRuleException("Arquivo de imagem vazio.");
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var formato = DetectarFormato(bytes)
            ?? throw new DomainRuleException("Formato de imagem invalido. Envie um arquivo JPG, PNG ou WebP.");

        // Confere que a extensao declarada bate com o conteudo real, para nao servir depois um
        // PNG com Content-Type de JPEG.
        var extensaoInformada = Path.GetExtension(nomeOriginal)?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(extensaoInformada) && !ExtensoesValidas.Contains(extensaoInformada))
        {
            throw new DomainRuleException("Extensao de arquivo nao suportada. Use .jpg, .png ou .webp.");
        }

        var arquivo = $"{Guid.NewGuid():N}{formato.Extensao}";
        var destino = Path.Combine(rootPath, arquivo);

        await using (var saida = new FileStream(
            destino,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(saida, cancellationToken);
        }

        logger.LogInformation(
            "Imagem de campanha gravada: {Arquivo} ({Bytes} bytes).", arquivo, buffer.Length);

        return arquivo;
    }

    public CampaignImageFile? Abrir(string arquivo)
    {
        // Rejeita qualquer nome que nao seja exatamente o que geramos. Isso barra travessia de
        // diretorio ("../../etc/passwd") antes de qualquer acesso ao filesystem.
        if (!NomeGeradoValido(arquivo))
        {
            return null;
        }

        var caminho = Path.Combine(rootPath, arquivo);
        if (!File.Exists(caminho))
        {
            return null;
        }

        var formato = FormatoPorExtensao(Path.GetExtension(caminho));
        if (formato is null)
        {
            return null;
        }

        var stream = new FileStream(
            caminho,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        return new CampaignImageFile(stream, formato.ContentType);
    }

    // Le no maximo maxBytes + 1: se conseguir passar do limite, o arquivo e grande demais. Nao
    // confiamos em Content-Length (o cliente controla o header).
    private async Task CopiarComLimiteAsync(Stream origem, Stream destino, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        int lidos;
        while ((lidos = await origem.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += lidos;
            if (total > maxBytes)
            {
                throw new DomainRuleException(
                    $"Imagem muito grande. O tamanho maximo e {maxBytes / (1024 * 1024)} MB.");
            }

            await destino.WriteAsync(buffer.AsMemory(0, lidos), cancellationToken);
        }
    }

    private sealed record FormatoImagem(string Extensao, string ContentType);

    private static FormatoImagem? DetectarFormato(ReadOnlySpan<byte> bytes)
    {
        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return new FormatoImagem(".jpg", "image/jpeg");
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return new FormatoImagem(".png", "image/png");
        }

        // WebP: "RIFF" ???? "WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return new FormatoImagem(".webp", "image/webp");
        }

        return null;
    }

    private static FormatoImagem? FormatoPorExtensao(string? extensao) =>
        extensao?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new FormatoImagem(".jpg", "image/jpeg"),
            ".png" => new FormatoImagem(".png", "image/png"),
            ".webp" => new FormatoImagem(".webp", "image/webp"),
            _ => null
        };

    /// <summary>
    /// Verifica que o nome tem exatamente o formato que <see cref="SalvarAsync"/> gera:
    /// 32 digitos hexadecimais + extensao conhecida. Publico e estatico para ser testavel.
    /// </summary>
    public static bool NomeGeradoValido(string? arquivo)
    {
        if (string.IsNullOrWhiteSpace(arquivo))
        {
            return false;
        }

        var extensao = Path.GetExtension(arquivo);
        if (!ExtensoesValidas.Contains(extensao.ToLowerInvariant()))
        {
            return false;
        }

        var nome = Path.GetFileNameWithoutExtension(arquivo);
        return nome.Length == 32 && nome.All(Uri.IsHexDigit);
    }
}
