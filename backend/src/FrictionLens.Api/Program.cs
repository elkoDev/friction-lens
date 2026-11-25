using System.Text.Json.Serialization;
using FrictionLens.Api.Analytics;
using FrictionLens.Api.Data;
using FrictionLens.Api.Endpoints;
using FrictionLens.Api.Extraction;
using FrictionLens.Api.Ingestion;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddOpenApi();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// Database provider switching: Sqlite (zero-cost local default) -> Postgres (cloud-ready)
var dbProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (string.Equals(dbProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var pgConn = builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres must be configured when Database:Provider=Postgres.");
        options.UseNpgsql(pgConn);
    }
    else
    {
        var sqliteConn = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=frictionlens.db";
        options.UseSqlite(sqliteConn);
    }
});

builder.Services.AddHttpClient("IngestionClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("FrictionLens-MVP/0.2 (+https://github.com/elkoDev/friction-lens)");
});

builder.Services.AddHttpClient("LlmClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
});

// Rate Limiter & Compliant Connectors
builder.Services.AddSingleton<ConnectorRateLimiter>();
builder.Services.AddScoped<ISourceConnector, HackerNewsConnector>();
builder.Services.AddScoped<ISourceConnector, GitHubConnector>();
builder.Services.AddScoped<ISourceConnector, DiscourseConnector>();
builder.Services.AddScoped<ISourceConnector, YouTubeConnector>();

// Pluggable Extraction Providers + Canonical Taxonomy Resolver
builder.Services.AddSingleton<HeuristicExtractionProvider>();
builder.Services.AddScoped<OllamaExtractionProvider>();
builder.Services.AddScoped<OpenAiCompatibleProvider>();
builder.Services.AddScoped<CanonicalTaxonomyResolver>();

builder.Services.AddScoped<IExtractionProvider>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var mode = cfg["Extraction:Provider"] ?? "Heuristic";
    return mode.ToLowerInvariant() switch
    {
        "ollama" => sp.GetRequiredService<OllamaExtractionProvider>(),
        "openaicompatible" or "openai" or "cloud" => sp.GetRequiredService<OpenAiCompatibleProvider>(),
        _ => sp.GetRequiredService<HeuristicExtractionProvider>()
    };
});

builder.Services.AddScoped<ExtractionPipeline>();
builder.Services.AddScoped<IntelligenceService>();
builder.Services.AddScoped<TeardownReportBuilder>();

// Background Ingestion Worker + SSE Progress Bus
builder.Services.AddSingleton<IIngestionQueue, IngestionQueue>();
builder.Services.AddSingleton<IJobProgressBus, JobProgressBus>();
builder.Services.AddHostedService<IngestionWorker>();

var app = builder.Build();

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await SeedData.EnsureSeededAsync(db);
}

app.MapFrictionLensApi();

app.Run();

public partial class Program;
