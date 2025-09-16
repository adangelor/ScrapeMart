// adangelor/scrapemart/ScrapeMart-307ccdf067def25afed98dbbac22027aa38c1af5/ScrapeMart/Services/CatalogOrchestratorService.cs
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

public sealed class CatalogOrchestratorService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CatalogOrchestratorService> _log;

    public CatalogOrchestratorService(
        IServiceProvider serviceProvider,
        ILogger<CatalogOrchestratorService> log)
    {
        _serviceProvider = serviceProvider;
        _log = log;
    }

    public async Task<SweepSummary> SweepFullCatalogAsync(string host, int? salesChannel, CancellationToken ct)
    {
        var summary = new SweepSummary(host);
        _log.LogInformation("Iniciando barrido directo de catálogo para {Host}...", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var syncService = scope.ServiceProvider.GetRequiredService<CatalogSyncService>();

        try
        {
            // Paso 1: Sincronizar categorías (esto es rápido y necesario)
            _log.LogInformation("Sincronizando categorías para {Host}...", host);
            var categoryCount = await syncService.SyncCategoriesAsync(host, 50, ct);
            summary.CategoriesSynced = categoryCount;
            _log.LogInformation("Se sincronizaron {Count} categorías.", categoryCount);

            // Paso 2: Barrer los productos de cada categoría y guardarlos DIRECTAMENTE
            _log.LogInformation("Iniciando barrido de productos directo a tablas finales...");
            var (total, upserts) = await syncService.SyncProductsAsync(
                host: host,
                categoryId: null, // Pasamos null para que procese TODAS las categorías del host
                pageSize: 50,
                maxPages: null,
                ct: ct);

            summary.TotalProductsFound = total;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falló el proceso de barrido directo para {Host}.", host);
            summary.ErrorMessage = ex.Message;
        }

        _log.LogInformation("Barrido directo para {Host} finalizado. Total de productos procesados: {ProductsCount}",
            host, summary.TotalProductsFound);
        return summary;
    }
}

public sealed record SweepSummary(string Host)
{
    public int CategoriesSynced { get; set; }
    public int TotalProductsFound { get; set; }
   
    public string? ErrorMessage { get; set; }
}