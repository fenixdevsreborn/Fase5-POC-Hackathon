using ConexaoSolidaria.Shared.Auth;
using ConexaoSolidaria.Shared.Validation;

namespace ConexaoSolidaria.Identity.Api.Domain;

public sealed class AppUser
{
    private AppUser()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string NomeCompleto { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string Cpf { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public string Role { get; private set; } = ApplicationRoles.Doador;

    public DateTimeOffset CriadoEm { get; private set; } = DateTimeOffset.UtcNow;

    public static AppUser CreateDoador(string nomeCompleto, string email, string cpf, string passwordHash)
    {
        if (!CpfValidator.IsValid(cpf))
        {
            throw new ArgumentException("CPF invalido.", nameof(cpf));
        }

        return new AppUser
        {
            NomeCompleto = nomeCompleto.Trim(),
            Email = NormalizeEmail(email),
            Cpf = CpfValidator.Normalize(cpf),
            PasswordHash = passwordHash,
            Role = ApplicationRoles.Doador
        };
    }

    public static AppUser CreateGestor(string nomeCompleto, string email, string cpf, string passwordHash)
    {
        return new AppUser
        {
            NomeCompleto = nomeCompleto.Trim(),
            Email = NormalizeEmail(email),
            Cpf = CpfValidator.Normalize(cpf),
            PasswordHash = passwordHash,
            Role = ApplicationRoles.GestorOng
        };
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
