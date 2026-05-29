namespace ConexaoSolidaria.Campaigns.Api.Domain;

public sealed class Donation
{
    private Donation()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid CampaignId { get; private set; }

    public Campaign? Campaign { get; private set; }

    public Guid DoadorId { get; private set; }

    public string DoadorEmail { get; private set; } = string.Empty;

    public decimal Valor { get; private set; }

    public DonationStatus Status { get; private set; } = DonationStatus.Pendente;

    public DateTimeOffset CriadaEm { get; private set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessadaEm { get; private set; }

    public static Donation Create(Guid campaignId, Guid doadorId, string doadorEmail, decimal valor)
    {
        if (valor <= 0)
        {
            throw new DomainRuleException("ValorDoacao deve ser maior que zero.");
        }

        return new Donation
        {
            CampaignId = campaignId,
            DoadorId = doadorId,
            DoadorEmail = doadorEmail.Trim().ToLowerInvariant(),
            Valor = valor
        };
    }

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
