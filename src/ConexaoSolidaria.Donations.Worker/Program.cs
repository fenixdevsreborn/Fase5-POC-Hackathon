using ConexaoSolidaria.Donations.Worker.Data;
using ConexaoSolidaria.Donations.Worker.Messaging;
using ConexaoSolidaria.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddDbContext<WorkerCampaignsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("CampaignsDb")));
builder.Services.AddHostedService<DonationConsumerWorker>();
builder.Services.AddHealthChecks();

var app = builder.Build();

await WorkerDatabaseInitializer.InitializeAsync(app.Services);

app.MapHealthChecks("/health");
app.MapMetrics();

app.Run();
