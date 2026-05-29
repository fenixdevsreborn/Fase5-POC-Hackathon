namespace ConexaoSolidaria.Donations.Worker.Domain;

public sealed class DonationRecord
{
    public Guid Id { get; private set; }

    public Guid CampaignId { get; private set; }

    public Guid DoadorId { get; private set; }

    public string DoadorEmail { get; private set; } = string.Empty;

    public decimal Valor { get; private set; }

    public DonationStatus Status { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    public DateTimeOffset? ProcessadaEm { get; private set; }

    public void MarkAsProcessed(DateTimeOffset processedAt)
    {
        Status = DonationStatus.Processada;
        ProcessadaEm = processedAt.ToUniversalTime();
    }

    public void MarkAsRejected(DateTimeOffset processedAt)
    {
        Status = DonationStatus.Rejeitada;
        ProcessadaEm = processedAt.ToUniversalTime();
    }
}
