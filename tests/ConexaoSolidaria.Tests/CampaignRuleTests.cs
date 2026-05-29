using ConexaoSolidaria.Campaigns.Api.Domain;

namespace ConexaoSolidaria.Tests;

public sealed class CampaignRuleTests
{
    [Fact]
    public void Create_ShouldRejectPastEndDate()
    {
        var now = DateTimeOffset.UtcNow;

        var exception = Assert.Throws<DomainRuleException>(() =>
            Campaign.Create(
                "Natal Solidario",
                "Arrecadacao para criancas",
                now.AddDays(-10),
                now.AddDays(-1),
                1000,
                CampaignStatus.Ativa,
                now));

        Assert.Contains("DataFim", exception.Message);
    }

    [Fact]
    public void Create_ShouldRejectZeroFinancialGoal()
    {
        var now = DateTimeOffset.UtcNow;

        var exception = Assert.Throws<DomainRuleException>(() =>
            Campaign.Create(
                "Natal Solidario",
                "Arrecadacao para criancas",
                now,
                now.AddDays(10),
                0,
                CampaignStatus.Ativa,
                now));

        Assert.Contains("MetaFinanceira", exception.Message);
    }

    [Fact]
    public void CanReceiveDonation_ShouldAcceptActiveCampaignInsidePeriod()
    {
        var now = DateTimeOffset.UtcNow;
        var campaign = Campaign.Create(
            "Natal Solidario",
            "Arrecadacao para criancas",
            now.AddDays(-1),
            now.AddDays(10),
            1000,
            CampaignStatus.Ativa,
            now);

        Assert.True(campaign.CanReceiveDonation(now));
    }

    [Fact]
    public void CanReceiveDonation_ShouldRejectCanceledCampaign()
    {
        var now = DateTimeOffset.UtcNow;
        var campaign = Campaign.Create(
            "Natal Solidario",
            "Arrecadacao para criancas",
            now,
            now.AddDays(10),
            1000,
            CampaignStatus.Cancelada,
            now);

        Assert.False(campaign.CanReceiveDonation(now));
    }
}
