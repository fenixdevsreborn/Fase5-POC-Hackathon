namespace ConexaoSolidaria.Campaigns.Api.Domain;

public sealed class Campaign
{
    private Campaign()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Titulo { get; private set; } = string.Empty;

    public string Descricao { get; private set; } = string.Empty;

    public DateTimeOffset DataInicio { get; private set; }

    public DateTimeOffset DataFim { get; private set; }

    public decimal MetaFinanceira { get; private set; }

    public decimal ValorTotalArrecadado { get; private set; }

    public CampaignStatus Status { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? AtualizadaEm { get; private set; }

    public static Campaign Create(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        DateTimeOffset now)
    {
        Validate(titulo, descricao, dataInicio, dataFim, metaFinanceira, now);

        return new Campaign
        {
            Titulo = titulo.Trim(),
            Descricao = descricao.Trim(),
            DataInicio = dataInicio.ToUniversalTime(),
            DataFim = dataFim.ToUniversalTime(),
            MetaFinanceira = metaFinanceira,
            Status = status
        };
    }

    public void Update(
        string titulo,
        string descricao,
        DateTimeOffset dataInicio,
        DateTimeOffset dataFim,
        decimal metaFinanceira,
        CampaignStatus status,
        DateTimeOffset now)
    {
        Validate(titulo, descricao, dataInicio, dataFim, metaFinanceira, now);

        Titulo = titulo.Trim();
        Descricao = descricao.Trim();
        DataInicio = dataInicio.ToUniversalTime();
        DataFim = dataFim.ToUniversalTime();
        MetaFinanceira = metaFinanceira;
        Status = status;
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
