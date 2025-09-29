using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Services;
using ScrapeMart.Storage;
using System.Text.Json;

namespace ScrapeMart.Endpoints;

public static class OperationsEndpoints
{
    public static RouteGroupBuilder MapOperationsEndpoints(this RouteGroupBuilder group)
    {
        // Master orchestrator - Proceso completo
        group.MapPost("/master-orchestrator/run",
            async ([FromServices] MasterOrchestratorService service, string? specificHost, CancellationToken ct) =>
            {
                await service.RunFullProcessAsync(specificHost);
                return Results.Ok(new { message = "Master orchestration completed", host = specificHost });
            })
            .WithName("RunMasterOrchestrator")
            .WithSummary("Ejecuta el proceso completo de scraping y sondeo para todos los retailers");

        // Catalog operations
        group.MapPost("/catalog/sweep",
            async ([FromServices] CatalogOrchestratorService service, string host, int? salesChannel, CancellationToken ct) =>
            {
                var result = await service.SweepFullCatalogAsync(host, salesChannel, ct);
                return Results.Ok(result);
            })
            .WithName("SweepCatalog")
            .WithSummary("Barre el catálogo completo de un retailer");

        // Brand scraping
        group.MapPost("/scraping/brands",
            async ([FromServices] BrandScrapingService service, string host, CancellationToken ct) =>
            {
                await service.ScrapeTrackedBrandsAsync(host, ct);
                return Results.Ok(new { message = "Brand scraping completed", host });
            })
            .WithName("ScrapeBrands")
            .WithSummary("Scraping dirigido por prefijos de marca");

        // EAN scraping
        group.MapPost("/scraping/eans",
            async ([FromServices] TargetedScrapingService service, string host, CancellationToken ct) =>
            {
                await service.ScrapeByEanListAsync(host, ct);
                return Results.Ok(new { message = "EAN scraping completed", host });
            })
            .WithName("ScrapeByEans")
            .WithSummary("Scraping dirigido por lista de EANs");

        // Store mapping (sweep)
        group.MapPost("/stores/sweep",
            async ([FromServices] VtexSweepService service, string? hostFilter, int[]? scCandidates, int? top, CancellationToken ct) =>
            {
                var result = await service.SweepAsync(hostFilter, scCandidates, top, ct);
                return Results.Ok(new { operationsProcessed = result, host = hostFilter });
            })
            .WithName("SweepStores")
            .WithSummary("Mapea sucursales físicas a pickup points online");

        // Availability probing
        group.MapPost("/availability/probe-all",
            async ([FromServices] AvailabilityOrchestratorService service, string host, CancellationToken ct) =>
            {
                await service.ProbeAllAsync(host, ct);
                return Results.Ok(new { message = "Availability probing completed", host });
            })
            .WithName("ProbeAllAvailability")
            .WithSummary("Sondea disponibilidad de todos los SKUs");

        group.MapPost("/availability/probe-eans",
            async ([FromServices] AvailabilityOrchestratorService service,
                   string host,
                   int minBatchSize = 20,
                   int maxBatchSize = 50,
                   int degreeOfParallelism = 8,
                   CancellationToken ct = default) =>
            {
                await service.ProbeEanListAsync(host, minBatchSize, maxBatchSize, degreeOfParallelism, ct);
                return Results.Ok(new { message = "EAN availability probing completed", host });
            })
            .WithName("ProbeEanAvailability")
            .WithSummary("Sondea disponibilidad por lista de EANs");

        // Product transcription from VTEX raw tables
        group.MapPost("/transcribe/vtex-products",
            async ([FromServices] VtexToProductsTranscriberService service,
                   string host,
                   int batchSize = 100,
                   CancellationToken ct = default) =>
            {
                var result = await service.TranscribeProductsAsync(host, batchSize, ct);
                return Results.Ok(result);
            })
            .WithName("TranscribeVtexProducts")
            .WithSummary("Transcribe productos desde tablas VTEX raw a tablas definitivas");
        // En tu OperationsEndpoints.cs
        group.MapPost("/availability/comprehensive-check",
            async ([FromServices] ComprehensiveAvailabilityService service,
                   string? specificRetailerHost,
                   CancellationToken ct) =>
            {
                var result = await service.RunComprehensiveAvailabilityCheckAsync(specificRetailerHost, ct);
                return Results.Ok(result);
            })
            .WithName("RunComprehensiveAvailabilityCheck")
            .WithSummary("Verifica disponibilidad de productos trackeados en TODAS las sucursales de TODAS las cadenas VTEX");
        return group;
    }


}

// ===================================================================================
// === ENDPOINTS DEL DASHBOARD (USANDO STORED PROCEDURES) ===
// ===================================================================================
public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboardEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/refresh-metrics", async ([FromServices] AppDb db) =>
        {
            await db.Database.ExecuteSqlRawAsync("EXEC [dbo].[sp_RefreshDashboardMetrics]");
            return Results.Ok(new { message = "Las métricas del dashboard han sido actualizadas." });
        })
        .WithName("RefreshDashboardMetrics")
        .WithSummary("Calcula y guarda todas las métricas del dashboard. Ejecutar periódicamente.");

        group.MapGet("/report", async ([AsParameters] ReportFilters filters, [FromServices] AppDb db) =>
        {
            var query = db.ProductAvailabilityReport.AsNoTracking();

            if (!string.IsNullOrEmpty(filters.Provincia))
                query = query.Where(r => r.Provincia == filters.Provincia);

            if (!string.IsNullOrEmpty(filters.Empresa))
                query = query.Where(r => r.Empresa == filters.Empresa);

            if (!string.IsNullOrEmpty(filters.Ean))
                query = query.Where(r => r.Ean == filters.Ean);

            if (!string.IsNullOrEmpty(filters.Owner))
            {
                var eansByOwner = db.ProductsToTrack.Where(p => p.Owner == filters.Owner).Select(p => p.EAN);
                query = query.Where(r => eansByOwner.Contains(r.Ean));
            }

            var totalItems = await query.CountAsync();
            var data = await query.Skip((filters.Page - 1) * filters.PageSize).Take(filters.PageSize).ToListAsync();

            return Results.Ok(new
            {
                TotalItems = totalItems,
                Page = filters.Page,
                PageSize = filters.PageSize,
                Data = data
            });
        })
        .WithName("GetReport")
        .WithSummary("Consulta la vista vw_ProductAvailabilityReport con filtros y paginación.");

        group.MapGet("/kpi/summary", async ([FromServices] AppDb db) =>
        {
            var results = await db.Database.SqlQuery<KpiSummary>($"EXEC [dbo].[sp_GetKpiSummary] @OwnerBrand = {"Adeco"}").ToListAsync();
            var summary = results.FirstOrDefault();
            return Results.Ok(summary);
        })
        .WithName("GetKpiSummary")
        .WithSummary("KPIs principales: Disponibilidad Adeco vs. Competencia y penetración en tiendas.");

        group.MapGet("/kpi/by-province", async ([FromServices] AppDb db) =>
        {
            var data = await db.Database.SqlQuery<AvailabilityByGroup>($"EXEC [dbo].[sp_GetAvailabilityByProvince]").ToListAsync();
            return Results.Ok(data);
        })
        .WithName("GetKpiByProvince")
        .WithSummary("Devuelve el % de disponibilidad de todos los productos, agrupado por provincia.");

        group.MapGet("/kpi/by-retailer", async ([FromServices] AppDb db) =>
        {
            var data = await db.Database.SqlQuery<AvailabilityByGroup>($"EXEC [dbo].[sp_GetAvailabilityByRetailer]").ToListAsync();
            return Results.Ok(data);
        })
        .WithName("GetKpiByRetailer")
        .WithSummary("Devuelve el % de disponibilidad de todos los productos, agrupado por cadena.");

        group.MapGet("/kpi/top-missing-products", async ([FromServices] AppDb db) =>
        {
            var data = await db.Database.SqlQuery<MissingProductInfo>($"EXEC [dbo].[sp_GetTopMissingProducts] @OwnerBrand = {"Adeco"}").ToListAsync();
            return Results.Ok(data);
        })
        .WithName("GetTopMissingProducts")
        .WithSummary("Ranking de los 20 productos propios con mayor % de faltante en góndola.");
        // agregar de optimizedavailabilityservice, los endpoints, ¿no?
        group.MapGet("/availability/optimized-check",
            async ([FromServices] OptimizedAvailabilityService service,
                   string? specificRetailerHost,
                   CancellationToken ct) =>
            {
                await service.ProbeAllEansInAllStoresAsync(specificRetailerHost,29, 50, 8,ct);
                return Results.Ok("Proceso terminado.");
            })
            .WithName("RunOptimizedAvailabilityCheck")
            .WithSummary("Verifica disponibilidad de productos trackeados en TODAS las sucursales de TODAS las cadenas VTEX, optimizado para grandes volúmenes.");
        group.MapPost("/debug/basic-connectivity",
    async ([FromServices] BasicDebuggingService service,
           string host,
           CancellationToken ct) =>
    {
        var result = await service.DiagnoseBasicConnectivityAsync(host, ct);
        return Results.Ok(result);
    })
    .WithName("DebugBasicConnectivity")
    .WithSummary("🚨 Diagnosis básica de conectividad");

        group.MapPost("/debug/test-configurations",
            async ([FromServices] BasicDebuggingService service,
                   string host,
                   CancellationToken ct) =>
            {
                var results = await service.TestDifferentConfigurationsAsync(host, ct);
                return Results.Ok(results);
            })
            .WithName("DebugTestConfigurations")
            .WithSummary("🧪 Test diferentes configuraciones de headers");
        group.MapPost("/debug/test-vtex-endpoints",
    async ([FromServices] VtexEndpointTesterService service,
           string host,
           CancellationToken ct) =>
    {
        var results = await service.TestVtexEndpointsAsync(host, ct);
        return Results.Ok(results);
    })
    .WithName("TestVtexEndpoints")
    .WithSummary("🧪 Test sistemático de endpoints VTEX");

        group.MapPost("/debug/find-working-product",
            async ([FromServices] VtexEndpointTesterService service,
                   string host,
                   CancellationToken ct) =>
            {
                var result = await service.FindWorkingProductAsync(host, ct);
                return Results.Ok(result);
            })
            .WithName("FindWorkingProduct")
            .WithSummary("🔍 Encontrar un producto que funcione para testing");

        group.MapPost("/test/working-flow",
            async ([FromServices] WorkingVtexFlowService service,
                   string host,
                   CancellationToken ct) =>
            {
                var result = await service.RunWorkingFlowAsync(host, ct);
                return Results.Ok(result);
            })
            .WithName("TestWorkingFlow")
            .WithSummary("Flujo que SÍ funciona - evita pickup points problemáticos");
        group.MapPost("/discovery/products-in-all-chains",
    async ([FromServices] VtexProductDiscoveryService service,
           string? specificHost,
           CancellationToken ct) =>
    {
        var result = await service.DiscoverAllProductsInAllChainsAsync(specificHost, ct);
        return Results.Ok(result);
    })
    .WithName("DiscoverProductsInAllChains")
    .WithSummary("🔍 Descubre TODOS los SKUs y sellers para productos de ProductsToTrack en TODAS las cadenas")
    .WithDescription(@"
        Busca todos los productos de la tabla ProductsToTrack en todas las cadenas VTEX habilitadas.
        Para cada producto encontrado, extrae todos sus SKUs y sellers.
        Persiste la información en las tablas VtexProducts, VtexSkus y VtexSkuSellers.
        
        Parámetros:
        - specificHost: (opcional) Si se especifica, solo procesa esa cadena. Ej: 'https://www.vea.com.ar'
        
        Este proceso es útil para:
        - Mapear qué productos existen en cada cadena
        - Identificar todos los SKUs disponibles para cada producto
        - Conocer todos los sellers que venden cada SKU
        - Preparar el terreno para posteriores chequeos de disponibilidad
        
        El proceso es paralelo pero controlado para no saturar las APIs de VTEX.");

        group.MapPost("/discovery/quick-search",
     async ([FromBody] QuickSearchRequest request,
            [FromServices] IHttpClientFactory httpFactory, // ✅ Inyectamos el factory directamente
            CancellationToken ct) =>
     {
         // NO CREES UN SCOPE MANUALMENTE. ASP.NET YA LO HACE.
         var http = httpFactory.CreateClient("vtexSession"); // Usamos el factory inyectado

         var results = new List<object>();

         foreach (var ean in request.Eans)
         {
             try
             {
                 var searchUrl = $"{request.Host.TrimEnd('/')}/api/catalog_system/pub/products/search?ft={ean}&_from=0&_to=0";
                 var response = await http.GetAsync(searchUrl, ct);

                 if (response.IsSuccessStatusCode)
                 {
                     var json = await response.Content.ReadAsStringAsync(ct);

                     if (!string.IsNullOrEmpty(json) && json != "[]")
                     {
                         using var doc = JsonDocument.Parse(json);
                         if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                         {
                             // ... tu código de parseo ...
                             results.Add(new { EAN = ean, Found = true, /* ...otros campos */ });
                         }
                         else
                         {
                             results.Add(new { EAN = ean, Found = false });
                         }
                     }
                     else
                     {
                         results.Add(new { EAN = ean, Found = false });
                     }
                 }
                 else
                 {
                     results.Add(new { EAN = ean, Found = false, Error = $"HTTP {response.StatusCode}" });
                 }
             }
             catch (Exception ex)
             {
                 results.Add(new { EAN = ean, Found = false, Error = ex.Message });
             }
         }

         return Results.Ok(new
         {
             Host = request.Host,
             TotalSearched = request.Eans.Count,
             TotalFound = results.Count(r => (bool)(r.GetType().GetProperty("Found")?.GetValue(r) ?? false)),
             Results = results
         });
     })
     .WithName("QuickProductSearch")
     .WithSummary("🔍 Búsqueda rápida de productos por EAN");

        return group;
    }
}



// ===================================================================================
// === DTOs PARA EL DASHBOARD ===
// ===================================================================================
public sealed class ReportFilters
{
    public string? Provincia { get; set; }
    public string? Empresa { get; set; }
    public string? Ean { get; set; }
    public string? Owner { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class KpiSummary
{
    public int TotalProductosAdeco { get; set; }
    public decimal DisponibilidadAdeco { get; set; }
    public decimal DisponibilidadCompetencia { get; set; }
    public int TotalTiendas { get; set; }
    public int TiendasConAdeco { get; set; }
    public decimal PenetracionAdeco { get; set; }
}

public sealed class AvailabilityByGroup
{
    public string? GroupName { get; set; }
    public int TotalProducts { get; set; }
    public int AvailableProducts { get; set; }
    public decimal AvailabilityPercentage { get; set; }
}

public sealed class MissingProductInfo
{
    public string? Ean { get; set; }
    public string? ProductName { get; set; }
    public string? Brand { get; set; }
    public int TotalStores { get; set; }
    public int StoresWithProduct { get; set; }
    public decimal MissingPercentage { get; set; }
}


// ===================================================================
// DTOs para el endpoint QuickSearch (agregar al final del archivo)
// ===================================================================
public sealed class QuickSearchRequest
{
    public string Host { get; set; } = default!;
    public List<string> Eans { get; set; } = new();
}
