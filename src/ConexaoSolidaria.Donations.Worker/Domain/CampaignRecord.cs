namespace ConexaoSolidaria.Donations.Worker.Domain;

public sealed class CampaignRecord
{
    public Guid Id { get; private set; }

    public string Titulo { get; private set; } = string.Empty;

    public string Descricao { get; private set; } = string.Empty;

    public DateTimeOffset DataInicio { get; private set; }

    public DateTimeOffset DataFim { get; private set; }

    public decimal MetaFinanceira { get; private set; }

    public decimal ValorTotalArrecadado { get; private set; }

    public CampaignStatus Status { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    public DateTimeOffset? AtualizadaEm { get; private set; }

    public bool CanReceiveDonation(DateTimeOffset now)
    {
        return Status == CampaignStatus.Ativa && DataFim >= now.ToUniversalTime();
    }

    public void AddDonation(decimal value)
    {
        ValorTotalArrecadado += value;
        AtualizadaEm = DateTimeOffset.UtcNow;
    }
}
