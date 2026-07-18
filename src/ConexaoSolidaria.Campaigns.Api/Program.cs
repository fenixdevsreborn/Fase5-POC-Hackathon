using System.Text;
using System.Text.Json.Serialization;
using Asp.Versioning;
using ConexaoSolidaria.Campaigns.Api.Data;
using ConexaoSolidaria.Campaigns.Api.Infrastructure;
using ConexaoSolidaria.Shared.Persistence;
using ConexaoSolidaria.Campaigns.Api.Messaging;
using ConexaoSolidaria.Campaigns.Api.Repositories;
using ConexaoSolidaria.Campaigns.Api.Services;
using ConexaoSolidaria.Contracts.Auth;
using ConexaoSolidaria.Contracts.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Polly;
using Polly.CircuitBreaker;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddDbContext<CampaignsDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("CampaignsDb"),
        // A4: execution strategy resiliente a falhas transitorias/failover do Postgres. Seguro aqui
        // porque a Campaigns.Api NAO abre transacoes explicitas (apenas SaveChangesAsync unico no
        // DoacoesController e no OutboxDispatcherWorker). Violacao de unique (idempotencia) NAO e
        // transitoria, entao a estrategia nao a reintenta e o catch de DbUpdateException segue valido.
        npgsql => npgsql.EnableRetryOnFailure()));

builder.Services.AddSingleton<IDonationEventPublisher, RabbitMqDonationEventPublisher>();
builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddScoped<ICampaignSearchRepository, ElasticCampaignSearchRepository>();

// Storage das imagens de campanha. Singleton: cria o diretorio uma unica vez no startup e nao
// guarda estado por requisicao. O RootPath vem do ambiente (volume montado no pod); vazio em dev
// cai numa pasta local sob o diretorio da aplicacao.
builder.Services.AddSingleton(serviceProvider =>
{
    var options = new CampaignImageOptions();
    serviceProvider.GetRequiredService<IConfiguration>()
        .GetSection(CampaignImageOptions.SectionName)
        .Bind(options);

    return options;
});
builder.Services.AddSingleton<ICampaignImageStorage, CampaignImageStorage>();

// B4: circuit breaker que protege o Elasticsearch. Apos falhas consecutivas o circuito abre e as
// buscas vao direto ao Postgres (fallback no CampaignService) por um periodo, sem martelar o ES.
builder.Services.AddResiliencePipeline(CampaignService.SearchPipelineKey, (pipeline, context) =>
{
    var logger = context.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("ElasticsearchCircuitBreaker");

    pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 4,
        SamplingDuration = TimeSpan.FromSeconds(10),
        BreakDuration = TimeSpan.FromSeconds(15),
        // Cancelamento (CancellationToken do request) nao conta como falha do ES.
        ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is not null and not OperationCanceledException),
        OnOpened = args =>
        {
            logger.LogWarning(
                "Circuito do Elasticsearch ABERTO por {BreakSeconds}s apos falhas consecutivas.",
                args.BreakDuration.TotalSeconds);
            return default;
        },
        OnClosed = _ =>
        {
            logger.LogInformation("Circuito do Elasticsearch FECHADO (servico recuperado).");
            return default;
        },
        OnHalfOpened = _ =>
        {
            logger.LogInformation("Circuito do Elasticsearch MEIO-ABERTO (testando recuperacao).");
            return default;
        }
    });
});

// A3: output caching para os reads publicos. TTL curto por endpoint via [OutputCache(Duration = 5)].
builder.Services.AddOutputCache();

// B6: versionamento por HEADER/QUERY (nunca por segmento de URL, para nao quebrar /api/campanhas,
// o Gateway e os testes). Sem versao informada -> assume 1.0 e as rotas atuais seguem identicas.
builder.Services.AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = ApiVersionReader.Combine(
            new HeaderApiVersionReader("x-api-version"),
            new QueryStringApiVersionReader("api-version"));
    })
    .AddMvc()
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = false;
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Conexao Solidaria - Campaigns API",
        Version = "v1"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Informe o token JWT no formato: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CampaignManagement", policy => policy.RequireRole(ApplicationRoles.GestorOng))
    .AddPolicy("DonationCreation", policy => policy.RequireRole(ApplicationRoles.Doador));

builder.Services.AddHealthChecks();

var app = builder.Build();

// B8: modo Job do k8s. Quando RunMigrationsOnly=true, aplica as migrations e ENCERRA o processo
// (sem subir o servidor web). Nos deployments, Migrations__RunOnStartup=false faz a API apenas
// aguardar o schema (o Job ja migrou); em dev/compose/testes o default migra no startup.
var runMigrationsOnly = string.Equals(
    builder.Configuration["RunMigrationsOnly"],
    "true",
    StringComparison.OrdinalIgnoreCase);

if (runMigrationsOnly)
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
    var migrationLogger = migrationScope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("CampaignsMigrationsJob");

    await CampaignsDatabaseInitializer.MigrateAsync(migrationDb, migrationLogger);
    return;
}

await CampaignsDatabaseInitializer.InitializeAsync(app.Services);

// Best-effort: garante o indice de busca (analisadores pt-BR) e faz backfill do Postgres quando o
// indice e criado agora. Falhas nao derrubam a API (a busca degrada para o PostgreSQL).
await ElasticsearchIndexInitializer.InitializeAsync(app.Services);

app.UseExceptionHandler();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpMetrics();

app.UseAuthentication();
app.UseAuthorization();

// A3: middleware do output cache. Depois de UseRouting/auth e antes dos endpoints.
app.UseOutputCache();

app.MapControllers();
// /health e /alive vem do ServiceDefaults (MapDefaultEndpoints); /metrics segue no prometheus-net.
app.MapDefaultEndpoints();
app.MapMetrics();

app.Run();


public partial class Program;
