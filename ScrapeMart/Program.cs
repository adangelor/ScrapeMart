using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Polly;
using Polly.Extensions.Http;
using ScrapeMart.Clients;
using ScrapeMart.Endpoints;
using ScrapeMart.Entities;
using ScrapeMart.Services;
using ScrapeMart.Storage;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
var proxyConfig = builder.Configuration.GetSection("Proxy");

// --- CONFIGURACIÓN DE SERVICIOS ---
builder.Services.Configure<VtexOptions>(builder.Configuration.GetSection("Vtex"));
builder.Services.AddHttpClient<VtexCatalogClient>();
builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpClient("vtexSession")
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler { UseCookies = true, CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = delegate { return true; } } };
        var proxyUrl = proxyConfig["Url"];
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            var proxy = new WebProxy(new Uri(proxyUrl));
            var username = proxyConfig["Username"];
            if (!string.IsNullOrEmpty(username)) { proxy.Credentials = new NetworkCredential(username, proxyConfig["Password"]); }
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        return handler;
    })
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError().WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

builder.Services.AddHttpClient(nameof(VtexPublicClient));

builder.Services.AddScoped<VtexSweepService>();
builder.Services.AddScoped<VtexProductSweepService>();
builder.Services.AddScoped<VtexFulltextCrawler>();
builder.Services.AddScoped<IVtexFulltextSink, VtexFulltextSink>();
builder.Services.AddScoped<VtexAvailabilityProbeService>();
builder.Services.AddScoped<ICatalogQueryService, CatalogQueryService>();
builder.Services.AddScoped<CatalogSyncService>();
builder.Services.AddScoped<CatalogOrchestratorService>();
builder.Services.AddScoped<AvailabilityOrchestratorService>(); // ¡NUEVO SERVICIO!

builder.Services.AddSingleton<VtexPublicClient>(serviceProvider =>
{
    var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    return new VtexPublicClient(factory);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => { c.SwaggerDoc("v1", new OpenApiInfo { Title = "ScrapeMart API", Version = "v1" }); });

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "ScrapeMart API v1"); c.RoutePrefix = string.Empty; });

// --- MAPEADO DE ENDPOINTS ---
app.MapVtexCatalogSweepEndpoints();
app.MapGroup("/api").WithOpenApi().WithTags("Catalog").MapCatalogEndpoints();
app.MapRetailerAvailabilityEndpoints();
MapRetailerAvailabilityProbe(app);

// --- ¡NUEVO ENDPOINT PARA EL SONDEO MASIVO! ---
app.MapPost("/ops/vtex/full-availability-probe", async (
    [FromServices] AvailabilityOrchestratorService orchestrator,
    string host,
    CancellationToken ct) =>
{
    // Esta es una operación de larga duración. No la esperes, se ejecuta en segundo plano.
    _ = orchestrator.ProbeAllAsync(host, ct);
    return Results.Accepted(value: new { message = "Proceso de sondeo masivo iniciado en segundo plano." });
})
.WithTags("Operations")
.WithSummary("Orquesta el sondeo masivo de disponibilidad para todos los SKUs y sucursales de un retailer.");
// --- FIN DEL NUEVO ENDPOINT ---


app.MapPost("/ops/vtex/full-catalog-sweep", async (
    [FromServices] CatalogOrchestratorService orchestrator,
    string host,
    int? sc,
    CancellationToken ct) =>
{
    var summary = await orchestrator.SweepFullCatalogAsync(host, sc, ct);
    return Results.Ok(summary);
})
.WithTags("Operations")
.WithSummary("Orquesta el barrido completo: sincroniza categorías y luego barre todos los productos.");

app.MapPost("/ops/vtex/fulltext-scan", async (ScanArgs args, [FromServices] VtexFulltextCrawler crawler, IHttpClientFactory httpFactory, CancellationToken ct) => {
    var http = httpFactory.CreateClient("vtexSession");
    int totalRequests = 0, totalProductsParsed = 0, lastHttpStatus = 0;
    foreach (var h in args.Hosts)
    {
        await crawler.WarmupAsync(http, h, ct);
        var (parsed, status) = await crawler.SweepOnceAsync(http, h, args.Query, args.From, args.To, ct);
        totalRequests++;
        totalProductsParsed += parsed;
        lastHttpStatus = status;
    }
    return Results.Ok(new { hosts = args.Hosts, totalRequests, totalProductsParsed, lastHttpStatus });
}).WithTags("Operations");

app.MapPost("/ops/vtex/sweep", async ([FromServices] VtexSweepService svc, string? host, string? sc, int? top, CancellationToken ct) => {
    int[] scList = string.IsNullOrWhiteSpace(sc) ? new[] { 1, 2 } : sc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
    var count = await svc.SweepAsync(host, scList, top, ct);
    return Results.Ok(new { executed = count, scTried = scList, host });
}).WithTags("Operations");

app.Run();

static void MapRetailerAvailabilityProbe(WebApplication app) { /* ... tu código existente ... */ }
public record ScanArgs(List<string> Hosts, string Query, int From = 0, int To = 49);
public record ProbeRequest(string SellerId, string PickupPointId, string PostalCode, string CountryCode = "AR", int SalesChannel = 1);
public static class RetailerDomainResolver { /* ... tu código existente ... */ }