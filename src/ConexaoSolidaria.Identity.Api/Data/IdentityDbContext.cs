using ConexaoSolidaria.Identity.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Identity.Api.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(user => user.Id);

            entity.Property(user => user.NomeCompleto).HasMaxLength(180).IsRequired();
            entity.Property(user => user.Email).HasMaxLength(180).IsRequired();
            entity.Property(user => user.Cpf).HasMaxLength(11).IsRequired();
            entity.Property(user => user.PasswordHash).HasMaxLength(200).IsRequired();
            entity.Property(user => user.Role).HasMaxLength(40).IsRequired();
            entity.Property(user => user.CriadoEm).IsRequired();

            entity.HasIndex(user => user.Email).IsUnique();
            entity.HasIndex(user => user.Cpf).IsUnique();
        });
    }
}
