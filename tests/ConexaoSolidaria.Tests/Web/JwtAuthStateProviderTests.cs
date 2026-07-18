using ConexaoSolidaria.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace ConexaoSolidaria.Tests.Web;

public sealed class JwtAuthStateProviderTests
{
    [Fact]
    public async Task ChaveAntiga_DegradaParaAnonimoELimpaToken_SemDerrubarCircuito()
    {
        var js = new BrowserStorageJsRuntime("payload-cifrado-com-chave-inexistente");
        var storage = new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider());
        var tokens = new TokenProvider { Token = "token-anterior" };
        var provider = new JwtAuthStateProvider(
            storage,
            tokens,
            NullLogger<JwtAuthStateProvider>.Instance);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.Null(tokens.Token);
        Assert.True(js.DeleteCalled, string.Join(", ", js.Invocations));
    }

    [Fact]
    public async Task LocalStorageIndisponivel_DegradaParaAnonimo_SemDerrubarCircuito()
    {
        var js = new BrowserStorageJsRuntime(new JSException("storage indisponivel"));
        var storage = new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider());
        var tokens = new TokenProvider { Token = "token-anterior" };
        var provider = new JwtAuthStateProvider(
            storage,
            tokens,
            NullLogger<JwtAuthStateProvider>.Instance);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.Null(tokens.Token);
    }

    private sealed class BrowserStorageJsRuntime : IJSRuntime
    {
        private readonly string? _storedValue;
        private readonly Exception? _getException;

        public BrowserStorageJsRuntime(string storedValue) => _storedValue = storedValue;
        public BrowserStorageJsRuntime(Exception getException) => _getException = getException;

        public bool DeleteCalled { get; private set; }
        public List<string> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Invocations.Add(identifier);
            if (identifier.EndsWith(".getItem", StringComparison.Ordinal))
            {
                if (_getException is not null)
                {
                    return ValueTask.FromException<TValue>(_getException);
                }

                return ValueTask.FromResult((TValue)(object?)_storedValue!);
            }

            if (identifier.EndsWith(".removeItem", StringComparison.Ordinal))
            {
                DeleteCalled = true;
                return ValueTask.FromResult(default(TValue)!);
            }

            throw new InvalidOperationException($"Chamada JS inesperada: {identifier}");
        }
    }
}
