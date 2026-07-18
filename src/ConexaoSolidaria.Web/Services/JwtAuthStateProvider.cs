using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace ConexaoSolidaria.Web.Services;

/// <summary>
/// AuthenticationStateProvider que persiste o JWT no ProtectedLocalStorage (lado servidor,
/// criptografado) e reconstroi o ClaimsPrincipal parseando o payload do token
/// (role, email, nameidentifier, name). Popula tambem o <see cref="TokenProvider"/>
/// para que o <see cref="ApiClient"/> anexe o Bearer.
/// </summary>
public sealed class JwtAuthStateProvider(
    ProtectedLocalStorage storage,
    TokenProvider tokenProvider,
    ILogger<JwtAuthStateProvider> logger) : AuthenticationStateProvider
{
    internal const string TokenKey = "cs_access_token";

    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var stored = await storage.GetAsync<string>(TokenKey);
            var token = stored.Success ? stored.Value : null;

            if (string.IsNullOrWhiteSpace(token))
            {
                tokenProvider.Token = null;
                return Anonymous;
            }

            if (!TryBuildPrincipal(token, out var principal))
            {
                // Token expirado/malformado nao deve permanecer no browser sendo
                // reavaliado em toda renderizacao do AuthorizeView.
                tokenProvider.Token = null;
                await TryRemoveStoredTokenAsync();
                return Anonymous;
            }

            tokenProvider.Token = token;
            return new AuthenticationState(principal);
        }
        catch (InvalidOperationException)
        {
            // ProtectedLocalStorage nao disponivel durante prerender (sem circuito JS).
            tokenProvider.Token = null;
            return Anonymous;
        }
        catch (JSException ex)
        {
            // localStorage indisponivel, circuito encerrando ou falha transitoria de JS.
            // Autenticacao degrada para anonimo sem derrubar o circuito inteiro.
            tokenProvider.Token = null;
            logger.LogDebug(ex, "Nao foi possivel ler o estado de autenticacao do navegador.");
            return Anonymous;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Payload cifrado por outro key ring do Data Protection (chaves regeneradas,
            // pasta de keys trocada, outro ApplicationName) ou JSON corrompido. O GetAsync
            // do ProtectedLocalStorage NAO trata isso: sem este catch a excecao sobe e
            // derruba o circuito Blazor logo apos conectar. Descarta o valor invalido para
            // que o proximo load ja comece limpo, em vez de repetir o erro a cada acesso.
            tokenProvider.Token = null;
            logger.LogWarning(
                "Token local descartado porque nao pode ser descriptografado pelo key ring atual.");
            await TryRemoveStoredTokenAsync();
            return Anonymous;
        }
    }

    private async Task TryRemoveStoredTokenAsync()
    {
        try
        {
            await storage.DeleteAsync(TokenKey);
        }
        catch (InvalidOperationException)
        {
            // Sem circuito JS disponivel; o valor invalido cai no proximo acesso interativo.
        }
        catch (JSException ex)
        {
            // Limpeza best-effort: uma falha do browser durante o descarte nunca deve
            // transformar logout/recuperacao de sessao em falha fatal do circuito.
            logger.LogDebug(ex, "Nao foi possivel remover o token local invalido.");
        }
    }

    /// <summary>Chamado pelo AuthService apos login/cadastro bem-sucedido.</summary>
    public void NotifyUserAuthentication(string token)
    {
        if (!TryBuildPrincipal(token, out var principal))
        {
            NotifyUserLogout();
            return;
        }

        tokenProvider.Token = token;
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));
    }

    /// <summary>Chamado pelo AuthService no logout.</summary>
    public void NotifyUserLogout()
    {
        tokenProvider.Token = null;
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }

    private static bool TryBuildPrincipal(string token, out ClaimsPrincipal principal)
    {
        principal = new ClaimsPrincipal(new ClaimsIdentity());

        var claims = ParseClaims(token);
        if (claims is null)
        {
            return false;
        }

        // Rejeita token expirado.
        var exp = claims.FirstOrDefault(c => c.Type == "exp")?.Value;
        if (long.TryParse(exp, out var expSeconds))
        {
            if (expSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return false;
            }
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "jwt",
            nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
        principal = new ClaimsPrincipal(identity);
        return true;
    }

    private static List<Claim>? ParseClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = Base64UrlDecode(parts[1]);
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload);
            if (map is null)
            {
                return null;
            }

            var claims = new List<Claim>();
            foreach (var (key, value) in map)
            {
                var type = MapClaimType(key);
                if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        claims.Add(new Claim(type, item.ToString()));
                    }
                }
                else
                {
                    claims.Add(new Claim(type, value.ToString()));
                }
            }

            return claims;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Normaliza as chaves curtas do JWT (e as URIs longas de ClaimTypes) para os
    // ClaimTypes esperados, garantindo IsInRole / Identity.Name funcionando.
    private static string MapClaimType(string key) => key switch
    {
        "role" or "roles" or ClaimTypes.Role => ClaimTypes.Role,
        "email" or ClaimTypes.Email => ClaimTypes.Email,
        "nameid" or "sub" or ClaimTypes.NameIdentifier => ClaimTypes.NameIdentifier,
        "unique_name" or "name" or ClaimTypes.Name => ClaimTypes.Name,
        _ => key,
    };

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }

        return Convert.FromBase64String(normalized);
    }
}
