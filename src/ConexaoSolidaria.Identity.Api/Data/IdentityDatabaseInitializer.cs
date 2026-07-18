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

        // B8 - Em dev/compose migra no startup (default true). No k8s o Job cuida das migrations
        // e o deployment recebe Migrations__RunOnStartup=false: aqui apenas aguardamos o schema antes do seed.
        var runOnStartup = !string.Equals(configuration["Migrations:RunOnStartup"], "false", StringComparison.OrdinalIgnoreCase);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                if (runOnStartup)
                {
                    await db.Database.MigrateAsync();
                }
                else
                {
                    await WaitForSchemaAsync(db, logger);
                }

                await SeedGestorAsync(db, configuration, logger);
                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Banco de identidade indisponivel. Tentativa {Attempt}/10.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    // B8 - Executado pelo Job do k8s (RunMigrationsOnly=true): aplica as migrations e encerra.
    public static async Task MigrateOnlyAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityDatabaseInitializer");

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                logger.LogInformation("Migrations do Identity aplicadas com sucesso (RunMigrationsOnly).");
                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Banco de identidade indisponivel para migracao. Tentativa {Attempt}/10.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    // Aguarda o schema estar pronto (Job de migracao ja executou) antes de tentar o seed.
    private static async Task WaitForSchemaAsync(IdentityDbContext db, ILogger logger)
    {
        if (!await db.Database.CanConnectAsync())
        {
            throw new InvalidOperationException("Banco de identidade ainda nao esta acessivel.");
        }

        var usersReady = await db.Database
            .SqlQuery<bool>($"SELECT (to_regclass('public.users') IS NOT NULL) AS \"Value\"")
            .SingleAsync();

        if (!usersReady)
        {
            throw new InvalidOperationException("Tabela 'users' ainda nao existe (aguardando Job de migracao).");
        }

        logger.LogInformation("Schema do Identity disponivel; prosseguindo com o seed.");
    }

    private static async Task SeedGestorAsync(IdentityDbContext db, IConfiguration configuration, ILogger logger)
    {
        if (await db.Users.AnyAsync(user => user.Role == Contracts.Auth.ApplicationRoles.GestorOng))
        {
            return;
        }

        var section = configuration.GetSection("Seed:Gestor");
        var email = section["Email"] ?? "gestor@conexaosolidaria.local";
        var password = section["Senha"];
        var cpf = section["Cpf"] ?? "52998224725";
        var nome = section["NomeCompleto"] ?? "Gestor ONG";

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "Seed do gestor ignorado: 'Seed:Gestor:Senha' nao configurado (defina via variavel de ambiente Seed__Gestor__Senha ou user-secrets).");
            return;
        }

        db.Users.Add(AppUser.CreateGestor(nome, email, cpf, BCrypt.Net.BCrypt.HashPassword(password)));
        await db.SaveChangesAsync();
    }
}
