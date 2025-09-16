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
builder.Services.AddScoped<VtexToProductsTranscriberService>();
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
builder.Services.AddScoped<MasterOrchestratorService>();
builder.Services.AddHttpClient(nameof(VtexPublicClient));
builder.Services.AddScoped<BrandScrapingService>();
builder.Services.AddScoped<VtexSweepService>();
builder.Services.AddScoped<VtexProductSweepService>();
builder.Services.AddScoped<VtexFulltextCrawler>();
builder.Services.AddScoped<IVtexFulltextSink, VtexFulltextSink>();
builder.Services.AddScoped<VtexAvailabilityProbeService>();
builder.Services.AddScoped<ICatalogQueryService, CatalogQueryService>();
builder.Services.AddScoped<CatalogSyncService>();
builder.Services.AddScoped<CatalogOrchestratorService>();
builder.Services.AddScoped<AvailabilityOrchestratorService>(); // ¡NUEVO SERVICIO!
builder.Services.AddScoped<TargetedScrapingService>();
builder.Services.AddSingleton<VtexPublicClient>(serviceProvider =>
{
    var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    return new VtexPublicClient(factory);
});

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ScrapeMart API",
        Version = "v1",
        Description = "Scrapemart API"
    });
});
var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ScrapeMart API v1");
    c.RoutePrefix = string.Empty;
});

// --- MAPEADO DE ENDPOINTS ---
app.MapVtexCatalogSweepEndpoints();
app.MapGroup("/api").WithOpenApi().WithTags("Catalog").MapCatalogEndpoints();
app.MapRetailerAvailabilityEndpoints();


app.MapPost("/ops/vtex/scrape-tracked-eans", async (
    string host,
    [FromServices] TargetedScrapingService scraper,
    CancellationToken ct) =>
{
    // Esta operación puede tardar, la ejecutamos en segundo plano
    _ = scraper.ScrapeByEanListAsync(host, ct);
    return Results.Accepted(value: new { message = $"Iniciado el scraping dirigido por EAN para {host}. El proceso se ejecuta en segundo plano." });
})
.WithTags("Operations")
.WithSummary("Busca y guarda en la BD solo los productos de la tabla 'ProductsToTrack' usando su EAN.");

app.MapPost("/ops/vtex/full-availability-probe", async (
    [FromServices] AvailabilityOrchestratorService orchestrator,
    string host,
    CancellationToken ct) =>
{
    try
    {
        // Ejecutamos el sondeo de forma síncrona para poder debuggear
        await orchestrator.ProbeAllAsync(host, ct);

        return Results.Ok(new
        {
            message = "Proceso de sondeo masivo completado exitosamente.",
            timestamp = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        // Capturamos cualquier excepción para debugging
        return Results.Problem(
            detail: ex.Message,
            title: "Error en el sondeo masivo",
            statusCode: 500,
            extensions: new Dictionary<string, object?>
            {
                ["exception"] = ex.ToString(),
                ["timestamp"] = DateTime.UtcNow
            });
    }
})
.WithTags("Operations")
.WithSummary("Orquesta el sondeo masivo de disponibilidad para todos los SKUs y sucursales de un retailer (modo debugging).");

app.MapPost("/ops/vtex/adeco-availability-probe", async (
    [FromServices] AvailabilityOrchestratorService orchestrator,
    string host,
    CancellationToken ct) =>
{
    try
    {

        await orchestrator.ProbeEanListAsync(host, 42, 63, 3, ct);

        return Results.Ok(new
        {
            message = "Proceso de sondeo masivo completado exitosamente.",
            timestamp = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        // Capturamos cualquier excepción para debugging
        return Results.Problem(
            detail: ex.Message,
            title: "Error en el sondeo masivo",
            statusCode: 500,
            extensions: new Dictionary<string, object?>
            {
                ["exception"] = ex.ToString(),
                ["timestamp"] = DateTime.UtcNow
            });
    }
})
.WithTags("Operations")
.WithSummary("Orquesta el sondeo masivo de disponibilidad para todos los SKUs y sucursales de un retailer (modo debugging).");

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

app.MapPost("/ops/vtex/fulltext-scan", async (ScanArgs args, [FromServices] VtexFulltextCrawler crawler, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
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

app.MapPost("/ops/vtex/sweep", async ([FromServices] VtexSweepService svc, string? host, string? sc, int? top, CancellationToken ct) =>
{
    int[] scList = string.IsNullOrWhiteSpace(sc) ? new[] { 1, 2 } : sc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
    var count = await svc.SweepAsync(host, scList, top, ct);
    return Results.Ok(new { executed = count, scTried = scList, host });
}).WithTags("Operations");
app.MapPost("/ops/vtex/scrape-by-brand-prefix", async (
    string host,
    [FromServices] BrandScrapingService scraper,
    CancellationToken ct) =>
{
    _ = scraper.ScrapeTrackedBrandsAsync(host, ct);
    return Results.Accepted(value: new { message = $"Iniciado el scraping por prefijo de marca para {host}. El proceso se ejecuta en segundo plano." });
})
.WithTags("Operations")
.WithSummary("Usa los prefijos de EAN de la tabla 'ProductsToTrack' para encontrar y guardar todos los productos de esas marcas.");

app.MapPost("/ops/run-full-orchestration", async (
    [FromServices] MasterOrchestratorService orchestrator,
    [FromQuery] string? host = null) =>
{
    // El 'host' es opcional. Si no se pasa, corre para todos los retailers habilitados.
    _ = orchestrator.RunFullProcessAsync(host);

    var message = host == null
        ? "Iniciado el proceso completo de orquestación para TODOS los retailers habilitados."
        : $"Iniciado el proceso completo de orquestación para el retailer: {host}.";

    return Results.Accepted(value: new { message = $"{message} El proceso se ejecuta en segundo plano." });
})
.WithTags("Operations")
.WithSummary("EJECUTA EL PROCESO COMPLETO: Scrapea catálogo, mapea sucursales y sondea disponibilidad.")
.WithDescription("Lee la VtexRetailersConfig y ejecuta la secuencia completa de 3 pasos para cada retailer habilitado (o para uno específico si se pasa como parámetro).");


// Ruta: Program.cs

// ===================================================================================
// === ENDPOINTS DEL DASHBOARD (VERSIÓN CON STORED PROCEDURES) ===
// ===================================================================================
// ===================================================================================
// === ENDPOINTS DEL DASHBOARD (USANDO STORED PROCEDURES) ===
// ===================================================================================
var dashboardGroup = app.MapGroup("/api/dashboard").WithTags("Dashboard").WithOpenApi();

dashboardGroup.MapPost("/refresh-metrics", async (AppDb db) => {
    await db.Database.ExecuteSqlRawAsync("EXEC [dbo].[sp_RefreshDashboardMetrics]");
    return Results.Ok(new { message = "Las métricas del dashboard han sido actualizadas." });
})
.WithSummary("Calcula y guarda todas las métricas del dashboard. Ejecutar periódicamente.");

dashboardGroup.MapGet("/report", async ([AsParameters] ReportFilters filters, AppDb db) =>
{
    var query = db.ProductAvailabilityReport.AsNoTracking();
    if (!string.IsNullOrEmpty(filters.Provincia)) query = query.Where(r => r.Provincia == filters.Provincia);
    if (!string.IsNullOrEmpty(filters.Empresa)) query = query.Where(r => r.Empresa == filters.Empresa);
    if (!string.IsNullOrEmpty(filters.Ean)) query = query.Where(r => r.Ean == filters.Ean);
    if (!string.IsNullOrEmpty(filters.Owner))
    {
        var eansByOwner = db.ProductsToTrack.Where(p => p.Owner == filters.Owner).Select(p => p.EAN);
        query = query.Where(r => eansByOwner.Contains(r.Ean));
    }
    var totalItems = await query.CountAsync();
    var data = await query.Skip((filters.Page - 1) * filters.PageSize).Take(filters.PageSize).ToListAsync();
    return Results.Ok(new { TotalItems = totalItems, Page = filters.Page, PageSize = filters.PageSize, Data = data });
})
.WithSummary("Consulta la vista vw_ProductAvailabilityReport con filtros y paginación.");

dashboardGroup.MapGet("/kpi/summary", async (AppDb db) =>
{
    var results = await db.Database.SqlQuery<KpiSummary>($"EXEC [dbo].[sp_GetKpiSummary] @OwnerBrand = {"Adeco"}").ToListAsync();
    var summary = results.FirstOrDefault();
    return Results.Ok(summary);
})
.WithSummary("KPIs principales: Disponibilidad Adeco vs. Competencia y penetración en tiendas.");

dashboardGroup.MapGet("/kpi/by-province", async (AppDb db) =>
{
    var data = await db.Database.SqlQuery<AvailabilityByGroup>($"EXEC [dbo].[sp_GetAvailabilityByProvince]").ToListAsync();
    return Results.Ok(data);
})
.WithSummary("Devuelve el % de disponibilidad de todos los productos, agrupado por provincia.");

dashboardGroup.MapGet("/kpi/by-retailer", async (AppDb db) =>
{
    var data = await db.Database.SqlQuery<AvailabilityByGroup>($"EXEC [dbo].[sp_GetAvailabilityByRetailer]").ToListAsync();
    return Results.Ok(data);
})
.WithSummary("Devuelve el % de disponibilidad de todos los productos, agrupado por cadena.");

dashboardGroup.MapGet("/kpi/top-missing-products", async (AppDb db) =>
{
    var data = await db.Database.SqlQuery<MissingProductInfo>($"EXEC [dbo].[sp_GetTopMissingProducts] @OwnerBrand = {"Adeco"}").ToListAsync();
    return Results.Ok(data);
})
.WithSummary("Ranking de los 20 productos propios con mayor % de faltante en góndola.");


app.Run();

public record ScanArgs(List<string> Hosts, string Query, int From = 0, int To = 49);

public class ReportFilters
{
    public string? Provincia { get; set; }
    public string? Empresa { get; set; } // Corresponde a la columna Empresa
    public string? Ean { get; set; }
    public string? Owner { get; set; } // Para filtrar por 'Adeco' o 'Competencia'
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;
}

// Clases para las respuestas de las métricas
public record KpiSummary(decimal AdecoAvailability, decimal CompetitorAvailability, int TotalStoresWithAdecoPresence, int TotalStoresScanned);
public record AvailabilityByGroup(string Group, decimal AvailabilityPercentage);
public record MissingProductInfo(string Ean, string Producto, string Marca, int StoresWithMissingStock, decimal MissingPercentage);