namespace ConexaoSolidaria.Shared.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = "ConexaoSolidaria";

    public string Audience { get; init; } = "ConexaoSolidaria";

    public string Secret { get; init; } = "local-dev-secret-change-me-with-at-least-32-characters";

    public int ExpiresMinutes { get; init; } = 120;
}
