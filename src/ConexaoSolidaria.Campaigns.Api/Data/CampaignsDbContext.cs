using ConexaoSolidaria.Campaigns.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Data;

public sealed class CampaignsDbContext(DbContextOptions<CampaignsDbContext> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<Donation> Donations => Set<Donation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Campaign>(entity =>
        {
            entity.ToTable("campaigns");
            entity.HasKey(campaign => campaign.Id);
            entity.Property(campaign => campaign.Titulo).HasMaxLength(160).IsRequired();
            entity.Property(campaign => campaign.Descricao).HasMaxLength(1000).IsRequired();
            entity.Property(campaign => campaign.MetaFinanceira).HasPrecision(18, 2).IsRequired();
            entity.Property(campaign => campaign.ValorTotalArrecadado).HasPrecision(18, 2).IsRequired();
            entity.Property(campaign => campaign.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(campaign => campaign.DataInicio).IsRequired();
            entity.Property(campaign => campaign.DataFim).IsRequired();
            entity.Property(campaign => campaign.CriadaEm).IsRequired();
        });

        modelBuilder.Entity<Donation>(entity =>
        {
            entity.ToTable("donations");
            entity.HasKey(donation => donation.Id);
            entity.Property(donation => donation.DoadorEmail).HasMaxLength(180).IsRequired();
            entity.Property(donation => donation.Valor).HasPrecision(18, 2).IsRequired();
            entity.Property(donation => donation.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(donation => donation.CriadaEm).IsRequired();

            entity.HasOne(donation => donation.Campaign)
                .WithMany()
                .HasForeignKey(donation => donation.CampaignId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(donation => donation.CampaignId);
            entity.HasIndex(donation => donation.Status);
        });
    }
}
