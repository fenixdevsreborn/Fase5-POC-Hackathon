var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Parametros de segredo da aplicacao.
// Em DEV os valores default vem de appsettings.Development.json (secao "Parameters"),
// para que `dotnet run --project src/ConexaoSolidaria.AppHost` suba tudo SEM setup manual.
// Em uso real, sobrescreva via user-secrets do AppHost:
//   dotnet user-secrets set "Parameters:jwt-secret" "<segredo forte>"
//   dotnet user-secrets set "Parameters:seed-manager-password" "<senha do gestor>"
// ---------------------------------------------------------------------------
var jwtSecret = builder.AddParameter("jwt-secret", secret: true);
var seedManagerPassword = builder.AddParameter("seed-manager-password", secret: true);

// ---------------------------------------------------------------------------
// Infraestrutura (containers gerenciados pelo Aspire; senhas auto-geradas).
// WithDataVolume() persiste os dados entre execucoes.
// ---------------------------------------------------------------------------
// Pin explicito da tag: evita que uma mudanca de versao do pacote Aspire troque a imagem
// (ex.: debian 18.3 <-> alpine 18.4) e quebre o layout do PGDATA persistido em WithDataVolume().
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18.3")
    .WithDataVolume();

// Os nomes das databases ("identitydb"/"campaignsdb") casam com GetConnectionString("IdentityDb"/"CampaignsDb")
// dos servicos, pois as chaves de configuracao do .NET sao case-insensitive.
var identityDb = postgres.AddDatabase("identitydb");
var campaignsDb = postgres.AddDatabase("campaignsdb");

var messaging = builder.AddRabbitMQ("messaging")
    .WithManagementPlugin()
    .WithDataVolume();

var search = builder.AddElasticsearch("search")
    .WithDataVolume();

// ---------------------------------------------------------------------------
// Identity API
// ---------------------------------------------------------------------------
var identityApi = builder.AddProject<Projects.ConexaoSolidaria_Identity_Api>("identity-api")
    .WithReference(identityDb)
    .WaitFor(identityDb)
    .WithEnvironment("Jwt__Secret", jwtSecret)
    .WithEnvironment("Jwt__Issuer", "ConexaoSolidaria")
    .WithEnvironment("Jwt__Audience", "ConexaoSolidaria")
    .WithEnvironment("Seed__Gestor__Senha", seedManagerPassword)
    .WithHttpHealthCheck("/health");

// ---------------------------------------------------------------------------
// Campaigns API
// ---------------------------------------------------------------------------
var campaignsApi = builder.AddProject<Projects.ConexaoSolidaria_Campaigns_Api>("campaigns-api")
    .WithReference(campaignsDb)
    .WithReference(messaging)
    .WithReference(search)
    // O repositorio de busca le a URL em "Elasticsearch:Url"; mapeamos a connection string do
    // recurso Aspire (que ja inclui as credenciais geradas) para essa chave.
    .WithEnvironment("Elasticsearch__Url", search)
    .WithEnvironment("Jwt__Secret", jwtSecret)
    .WithEnvironment("Jwt__Issuer", "ConexaoSolidaria")
    .WithEnvironment("Jwt__Audience", "ConexaoSolidaria")
    .WaitFor(campaignsDb)
    .WaitFor(messaging)
    .WaitFor(search)
    .WithHttpHealthCheck("/health");

// ---------------------------------------------------------------------------
// Donations Worker (consumidor assincrono; sem endpoints externos)
// ---------------------------------------------------------------------------
builder.AddProject<Projects.ConexaoSolidaria_Donations_Worker>("donations-worker")
    .WithReference(campaignsDb)
    .WithReference(messaging)
    .WaitFor(campaignsDb)
    .WaitFor(messaging);

// ---------------------------------------------------------------------------
// Gateway (BFF/reverse proxy) e Web (frontend) — expostos publicamente.
// ---------------------------------------------------------------------------
var gateway = builder.AddProject<Projects.ConexaoSolidaria_Gateway>("gateway")
    .WithReference(identityApi)
    .WithReference(campaignsApi)
    .WaitFor(identityApi)
    .WaitFor(campaignsApi)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.ConexaoSolidaria_Web>("web")
    .WithReference(gateway)
    // O Web consome o fanout de notificacoes (conexao-solidaria.notifications) para
    // empurrar atualizacoes em tempo real via SignalR; referencia o RabbitMQ para obter
    // a connection string. Se o broker cair, o consumer e resiliente e o polling e o fallback.
    .WithReference(messaging)
    .WaitFor(gateway)
    .WaitFor(messaging)
    .WithExternalHttpEndpoints();

builder.Build().Run();
