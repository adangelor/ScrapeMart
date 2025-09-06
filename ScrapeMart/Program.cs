// File: Program.cs
using Microsoft.AspNetCore.Mvc; // <-- AÑADIR ESTE USING para los atributos
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using ScrapeMart.Clients;
using ScrapeMart.Endpoints;
using ScrapeMart.Services;
using ScrapeMart.Storage;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// --- CONFIGURACIÓN DE SERVICIOS (Dependency Injection) ---

// (El resto de la configuración de servicios queda exactamente igual)
builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpClient("vtexSession")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    });

builder.Services.AddHttpClient(nameof(VtexPublicClient));

builder.Services.AddScoped<VtexSweepService>();
builder.Services.AddScoped<VtexProductSweepService>();
builder.Services.AddScoped<VtexFulltextCrawler>();
builder.Services.AddScoped<IVtexFulltextSink, VtexFulltextSink>();
builder.Services.AddScoped<VtexAvailabilityProbeService>();
builder.Services.AddScoped<ICatalogQueryService, CatalogQueryService>();
builder.Services.AddSingleton<VtexPublicClient>(serviceProvider =>
{
    var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    return new VtexPublicClient(factory);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ScrapeMart API", Version = "v1" });
});


// --- CONSTRUCCIÓN Y PIPELINE DE LA APLICACIÓN ---

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ScrapeMart API v1");
    c.RoutePrefix = string.Empty;
});

// --- MAPEADO DE ENDPOINTS ---

app.MapVtexCatalogSweepEndpoints();
app.MapGroup("/api")
   .WithOpenApi()
   .WithTags("Catalog")
   .MapCatalogEndpoints();

app.MapRetailerAvailabilityEndpoints();
MapRetailerAvailabilityProbe(app);


// CORRECCIÓN 1: Añadir [FromServices] a los parámetros que son servicios
app.MapPost("/ops/vtex/fulltext-scan", async (
    ScanArgs args,
    [FromServices] VtexFulltextCrawler crawler,
    IHttpClientFactory httpFactory,
    CancellationToken ct) =>
{
    var http = httpFactory.CreateClient("vtexSession");
    int totalRequests = 0, totalProductsParsed = 0, lastHttpStatus = 0;

    foreach (var host in args.Hosts)
    {
        await crawler.WarmupAsync(http, host, ct);
        var (parsed, status) = await crawler.SweepOnceAsync(
            http, host, args.Query, args.From, args.To, ct);

        totalRequests++;
        totalProductsParsed += parsed;
        lastHttpStatus = status;
    }

    return Results.Ok(new { hosts = args.Hosts, totalRequests, totalProductsParsed, lastHttpStatus });
})
.WithTags("Operations");


// CORRECCIÓN 2: Añadir [FromServices] aquí también
app.MapPost("/ops/vtex/sweep", async ([FromServices] VtexSweepService svc, string? host, string? sc, int? top, CancellationToken ct) =>
{
    int[] scList = string.IsNullOrWhiteSpace(sc)
        ? new[] { 1, 2 }
        : sc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();
    var count = await svc.SweepAsync(host, scList, top, ct);
    return Results.Ok(new { executed = count, scTried = scList, host });
})
.WithTags("Operations");


app.Run();


// --- DEFINICIONES LOCALES (para mantener el Program.cs limpio) ---

static void MapRetailerAvailabilityProbe(WebApplication app)
{
    app.MapPost("/retailers/{retailer}/skus/{skuId}/probe-availability",
        async (
            string retailer,
            string skuId,
            [FromBody] ProbeRequest body,
            [FromServices] VtexAvailabilityProbeService probeService) =>
        {
            var host = RetailerDomainResolver.Resolve(retailer);
            if (host is null)
                return Results.BadRequest(new { error = "retailer_not_supported", retailer });

            var result = await probeService.ProbePickupAsync(
                host: host,
                salesChannel: body.SalesChannel,
                skuId: skuId,
                sellerId: body.SellerId,
                pickupPointId: body.PickupPointId,
                countryCode: body.CountryCode,
                postalCode: body.PostalCode);

            return Results.Ok(result);
        })
    .WithTags("Retailer Availability")
    .WithSummary("Realiza un sondeo de stock máximo para un SKU en un punto de retiro específico.");
}

public record ScanArgs(List<string> Hosts, string Query, int From = 0, int To = 49);
public record ProbeRequest(string SellerId, string PickupPointId, string PostalCode, string CountryCode = "AR", int SalesChannel = 1);

public static class RetailerDomainResolver
{
    public static string? Resolve(string retailer) => retailer.ToLowerInvariant() switch
    {
        "vea" => "https://www.vea.com.ar/",
        "jumbo" => "https://www.jumbo.com.ar/",
        "disco" => "https://www.disco.com.ar/",
        "carrefour" => "https://www.carrefour.com.ar/",
        "libertad" => "https://www.hiperlibertad.com.ar/",
        _ => null
    };
}