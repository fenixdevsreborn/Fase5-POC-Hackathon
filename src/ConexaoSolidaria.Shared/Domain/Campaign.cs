namespace ConexaoSolidaria.Shared.Domain;

public sealed class Campaign
{
    private Campaign()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Titulo { get; private set; } = string.Empty;

    /// <summary>
    /// Titulo em forma canonica (trim + minusculas + espacos internos colapsados). Existe como
    /// coluna propria porque e ela que sustenta o indice UNICO de titulo: comparar o titulo cru
    /// deixaria passar "Cestas de Natal" e "cestas de natal " como campanhas distintas.
    /// Mantido apenas pelo dominio (Create/Update) — nunca setado de fora.
    /// </summary>
    public string TituloNormalizado { get; private set; } = string.Empty;

    public string Descricao { get; private set; } = string.Empty;

    /// <summary>
    /// Nome do arquivo de imagem da campanha no storage (ex.: "3f2a....jpg"), ou null quando o
    /// gestor nao enviou foto — nesse caso a UI cai na imagem ilustrativa por categoria.
    /// Guarda so o nome: a URL publica e montada por quem serve (GET /api/campanhas/imagens/{arquivo}).
    /// </summary>
    public string? Imagem { get; private set; }

    public DateTimeOffset DataInicio { get; private set; }

    public DateTimeOffset DataFim { get; private set; }

    public decimal MetaFinanceira { get; private set; }

    public decimal ValorTotalArrecadado { get; private set; }

    public CampaignStatus Status { get; private set; }

    public CampaignCategory Categoria { get; private set; } = CampaignCategory.Outros;

    public DateTimeOffset CriadaEm { get; private set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? AtualizadaEm { get; private set; }

    public static Campaign Create(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        DateTimeOffset now,
        CampaignCategory categoria = CampaignCategory.Outros,
        string? imagem = null)
    {
        Validate(titulo, descricao, dataInicio, dataFim, metaFinanceira, now);

        return new Campaign
        {
            Titulo = titulo.Trim(),
            TituloNormalizado = NormalizarTitulo(titulo),
            Descricao = descricao.Trim(),
            DataInicio = dataInicio.ToUniversalTime(),
            DataFim = dataFim.ToUniversalTime(),
            MetaFinanceira = metaFinanceira,
            Status = status,
            Categoria = categoria,
            Imagem = string.IsNullOrWhiteSpace(imagem) ? null : imagem.Trim()
        };
    }

    /// <summary>
    /// Forma canonica usada na comparacao de unicidade: trim, minusculas (invariante) e espacos
    /// internos colapsados. Publico e estatico para que a API possa consultar o banco pelo mesmo
    /// criterio antes de tentar inserir (mensagem de erro amigavel em vez de violacao de indice).
    /// </summary>
    public static string NormalizarTitulo(string titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo))
        {
            return string.Empty;
        }

        var partes = titulo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', partes).ToLowerInvariant();
    }

    public void Update(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        DateTimeOffset now,
        CampaignCategory categoria = CampaignCategory.Outros,
        string? imagem = null)
    {
        Validate(titulo, descricao, dataInicio, dataFim, metaFinanceira, now);

        // Se o status mudou, aplica as regras de transicao de dominio em vez de setar o valor cru.
        if (status != Status)
        {
            TransitionTo(status);
        }

        Titulo = titulo.Trim();
        TituloNormalizado = NormalizarTitulo(titulo);
        // null preserva a imagem atual (edicao sem mexer na foto); string vazia remove.
        if (imagem is not null)
        {
            Imagem = string.IsNullOrWhiteSpace(imagem) ? null : imagem.Trim();
        }

        Descricao = descricao.Trim();
        DataInicio = dataInicio.ToUniversalTime();
        DataFim = dataFim.ToUniversalTime();
        MetaFinanceira = metaFinanceira;
        Categoria = categoria;
        AtualizadaEm = DateTimeOffset.UtcNow;
    }

    /// <summary>Publica/ativa a campanha (destino <see cref="CampaignStatus.Ativa"/>).</summary>
    public void Publicar() => TransitionTo(CampaignStatus.Ativa);

    /// <summary>Alias de <see cref="Publicar"/> (destino <see cref="CampaignStatus.Ativa"/>).</summary>
    public void Ativar() => TransitionTo(CampaignStatus.Ativa);

    /// <summary>Conclui a campanha (Ativa -> Concluida).</summary>
    public void Concluir() => TransitionTo(CampaignStatus.Concluida);

    /// <summary>Cancela a campanha (Ativa -> Cancelada).</summary>
    public void Cancelar() => TransitionTo(CampaignStatus.Cancelada);

    // Regras de transicao de status. Ativa e o unico estado de origem que admite mudanca:
    // Ativa -> Concluida e Ativa -> Cancelada. Concluida e Cancelada sao terminais (nao voltam
    // para Ativa nem entre si). Transicoes para o mesmo estado sao no-op idempotentes.
    private void TransitionTo(CampaignStatus destino)
    {
        if (destino == Status)
        {
            return;
        }

        var permitido = Status switch
        {
            CampaignStatus.Ativa => destino is CampaignStatus.Concluida or CampaignStatus.Cancelada,
            _ => false
        };

        if (!permitido)
        {
            throw new DomainRuleException(
                $"Transicao de status invalida: {Status} -> {destino}.");
        }

        Status = destino;
        AtualizadaEm = DateTimeOffset.UtcNow;
    }

    public bool CanReceiveDonation(DateTimeOffset now)
    {
        return Status == CampaignStatus.Ativa && DataFim >= now.ToUniversalTime();
    }

    public void AddDonation(decimal valor)
    {
        if (valor <= 0)
        {
            throw new DomainRuleException("Valor da doacao deve ser maior que zero.");
        }

        ValorTotalArrecadado += valor;
        AtualizadaEm = DateTimeOffset.UtcNow;
    }

    private static void Validate(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(titulo))
        {
            throw new DomainRuleException("Titulo e obrigatorio.");
        }

        if (string.IsNullOrWhiteSpace(descricao))
        {
            throw new DomainRuleException("Descricao e obrigatoria.");
        }

        if (dataFim.ToUniversalTime() < now.ToUniversalTime())
        {
            throw new DomainRuleException("DataFim nao pode estar no passado.");
        }

        if (dataFim.ToUniversalTime() < dataInicio.ToUniversalTime())
        {
            throw new DomainRuleException("DataFim deve ser maior ou igual a DataInicio.");
        }

        if (metaFinanceira <= 0)
        {
            throw new DomainRuleException("MetaFinanceira deve ser maior que zero.");
        }
    }
}
