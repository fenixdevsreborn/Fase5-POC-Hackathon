using ConexaoSolidaria.Contracts.Messaging;
using ConexaoSolidaria.Web.Components;
using ConexaoSolidaria.Web.Services;
using ConexaoSolidaria.Web.Services.Ai;
using ConexaoSolidaria.Web.Services.Import;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Aspire: service discovery, resilience, health checks, OpenTelemetry.
builder.AddServiceDefaults();

// MudBlazor (dialog/snackbar/popover/resize services etc.).
builder.Services.AddMudServices();

// Blazor Web App - Interactive Server.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Armazenamento protegido do JWT (lado servidor, por circuito).
// Data Protection keys persistidas para funcionar em multi-replica (k8s):
// tokens cifrados por uma replica precisam ser decifrados por qualquer outra.
// Em producao, KeysPath deve apontar para um volume compartilhado/PVC montado
// em todas as replicas do web. Em dev, cai para uma pasta local (./keys).
var keysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("ConexaoSolidaria");
builder.Services.AddScoped<ProtectedLocalStorage>();

// Autenticacao/estado do usuario.
builder.Services.AddScoped<TokenProvider>();
builder.Services.AddScoped<JwtAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthStateProvider>());
builder.Services.AddScoped<AuthService>();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// Cliente tipado do Gateway (base address resolvido via service discovery do Aspire).
// Resiliencia (timeout + retry em transitorios + circuit breaker) NAO e configurada
// aqui: o AddServiceDefaults ja aplica AddStandardResilienceHandler() via
// ConfigureHttpClientDefaults a TODOS os HttpClients criados pela factory, incluindo
// este ApiClient tipado. Nao duplicamos o handler para evitar retries em cascata —
// importante porque o polling de status da doacao faz chamadas repetidas.
builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri("http://gateway");
});

// Notificacoes em tempo real (best-effort) via RabbitMQ fanout -> circuito SignalR do Blazor.
// O dispatcher (singleton) reemite as notificacoes para as telas conectadas; o consumidor
// (BackgroundService) conecta ao broker com reconexao resiliente. Se o RabbitMQ estiver
// ausente/indisponivel, o app segue funcionando (o polling do DonationStatus e o fallback).
builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddHostedService<NotificationConsumer>();

// IA (Assistente Solidario + criacao assistida de campanhas) via Microsoft Agent Framework.
// Sem OPENAI_API_KEY o app sobe normalmente e a UI esconde as features (mesmo espirito do
// RabbitMQ opcional acima). Os servicos sao SEMPRE registrados para os @inject dos
// componentes nunca falharem; o gate e AiChatClientProvider.Enabled.
builder.Services.Configure<AiOptions>(options =>
{
    builder.Configuration.GetSection(AiOptions.SectionName).Bind(options);
    options.ApiKey ??= builder.Configuration["OPENAI_API_KEY"];
});
builder.Services.AddSingleton<AiChatClientProvider>();   // IChatClient singleton (thread-safe)
builder.Services.AddScoped<AssistantTools>();            // usa ApiClient/TokenProvider do circuito
builder.Services.AddScoped<AssistantChatService>();      // agente + thread por circuito Blazor
builder.Services.AddScoped<CampaignDraftService>();

// Importacao de planilhas de campanha (.xlsx/.csv). Sem estado por requisicao: o parse acontece
// inteiro em memoria e o resultado vai para a tela de lote, que so persiste apos revisao.
builder.Services.AddSingleton<CampaignImportService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Proxy das imagens de campanha. O browser so enxerga a Web (o Gateway nao e publicado para fora),
// entao esta rota busca o arquivo na Campaigns.Api via Gateway e repassa os bytes. Anonima de
// proposito: a vitrine publica precisa renderizar as fotos sem login.
app.MapGet("/imagens/campanhas/{arquivo}", async (
    string arquivo,
    ApiClient api,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var imagem = await api.BaixarImagemCampanhaAsync(arquivo, cancellationToken);
    if (imagem is null)
    {
        return Results.NotFound();
    }

    // O nome do arquivo e imutavel (trocar a foto gera outro nome), entao cache longo e seguro.
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Stream(imagem.Value.Content, imagem.Value.ContentType);
});

// Modelo da planilha de importacao. Gerado sob demanda para o cabecalho nunca sair de sincronia
// com o que o CampaignImportService sabe ler.
app.MapGet("/modelo-campanhas.xlsx", () => Results.File(
    CampaignImportService.GerarModeloExcel(),
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    "modelo-campanhas.xlsx"));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
