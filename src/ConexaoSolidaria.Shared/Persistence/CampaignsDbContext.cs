using ConexaoSolidaria.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Shared.Persistence;

/// <summary>
/// Context EF unico sobre o 'campaignsdb', compartilhado pela Campaigns.Api (escrita de campanhas,
/// doacoes, outbox e chaves de idempotencia) e pelo Donations.Worker (leitura/atualizacao de doacoes
/// e campanhas + dedup via processed_messages). Todas as tabelas ficam mapeadas aqui.
/// </summary>
public sealed class CampaignsDbContext(DbContextOptions<CampaignsDbContext> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<Donation> Donations => Set<Donation>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<DonationIdempotencyKey> DonationIdempotencyKeys => Set<DonationIdempotencyKey>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    /// <summary>
    /// Read model para dashboards (CQRS leve). POPULADO pelo Donations.Worker; a Campaigns.Api so LE.
    /// </summary>
    public DbSet<CampaignStats> CampaignStats => Set<CampaignStats>();

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
            entity.Property(campaign => campaign.Categoria)
                .HasConversion<string>()
                .HasMaxLength(40)
                .HasDefaultValue(CampaignCategory.Outros)
                .IsRequired();
            entity.Property(campaign => campaign.DataInicio).IsRequired();
            entity.Property(campaign => campaign.DataFim).IsRequired();
            entity.Property(campaign => campaign.CriadaEm).IsRequired();

            // Cobre a query de transparencia/listagem publica (Status = Ativa AND DataFim >= now,
            // ordenada por DataFim). Indice composto na ordem (Status, DataFim).
            entity.HasIndex(campaign => new { campaign.Status, campaign.DataFim });
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
            // Consultas por doador (historico de doacoes de um usuario).
            entity.HasIndex(donation => donation.DoadorId);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(200).IsRequired();
            entity.Property(message => message.SchemaVersion).IsRequired();
            entity.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.OccurredAtUtc).IsRequired();
            entity.Property(message => message.PublishedAtUtc);
            entity.Property(message => message.Attempts).IsRequired();
            entity.Property(message => message.NextAttemptAtUtc);
            entity.Property(message => message.LastError);
            entity.Property(message => message.CorrelationId).HasMaxLength(100).IsRequired();

            // Indice usado pelo dispatcher para varrer mensagens pendentes.
            entity.HasIndex(message => new { message.PublishedAtUtc, message.NextAttemptAtUtc });

            // Indice PARCIAL: o dispatcher so varre pendentes (PublishedAtUtc IS NULL). Um indice
            // filtrado sobre NextAttemptAtUtc mantem-se pequeno (nao indexa mensagens ja publicadas)
            // e acelera a selecao do proximo lote. Nome de coluna entre aspas (Postgres, PascalCase).
            entity.HasIndex(message => message.NextAttemptAtUtc)
                .HasFilter("\"PublishedAtUtc\" IS NULL");
        });

        modelBuilder.Entity<DonationIdempotencyKey>(entity =>
        {
            entity.ToTable("donation_idempotency_keys");
            entity.HasKey(key => key.Key);
            entity.Property(key => key.Key).HasMaxLength(120).IsRequired();
            entity.Property(key => key.DonationId).IsRequired();
            entity.Property(key => key.CreatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.ToTable("processed_messages");
            entity.HasKey(message => message.EventId);
            entity.Property(message => message.EventId).ValueGeneratedNever();
            entity.Property(message => message.ProcessedAtUtc).IsRequired();
        });

        modelBuilder.Entity<CampaignStats>(entity =>
        {
            entity.ToTable("campaign_stats");
            entity.HasKey(stats => stats.CampaignId);
            entity.Property(stats => stats.CampaignId).ValueGeneratedNever();
            entity.Property(stats => stats.Titulo).HasMaxLength(160).IsRequired();
            entity.Property(stats => stats.MetaFinanceira).HasPrecision(18, 2).IsRequired();
            entity.Property(stats => stats.TotalArrecadado).HasPrecision(18, 2).IsRequired();
            entity.Property(stats => stats.DoacoesProcessadas).IsRequired();
            entity.Property(stats => stats.AtualizadoEm).IsRequired();
        });
    }
}
