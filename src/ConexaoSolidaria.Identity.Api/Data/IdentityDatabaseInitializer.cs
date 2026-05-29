using ConexaoSolidaria.Identity.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Identity.Api.Data;

public static class IdentityDatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityDatabaseInitializer");

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();
                await SeedGestorAsync(db, configuration);
                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Banco de identidade indisponivel. Tentativa {Attempt}/10.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static async Task SeedGestorAsync(IdentityDbContext db, IConfiguration configuration)
    {
        if (await db.Users.AnyAsync(user => user.Role == Shared.Auth.ApplicationRoles.GestorOng))
        {
            return;
        }

        var section = configuration.GetSection("Seed:Gestor");
        var email = section["Email"] ?? "gestor@conexaosolidaria.local";
        var password = section["Senha"] ?? "Gestor@123456";
        var cpf = section["Cpf"] ?? "52998224725";
        var nome = section["NomeCompleto"] ?? "Gestor ONG";

        db.Users.Add(AppUser.CreateGestor(nome, email, cpf, BCrypt.Net.BCrypt.HashPassword(password)));
        await db.SaveChangesAsync();
    }
}
