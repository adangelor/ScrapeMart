using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

/// <summary>
/// Servicio de alto nivel para orquestar operaciones complejas de barrido y sincronización.
/// </summary>
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

    /// <summary>
    /// Ejecuta el proceso completo de sincronización de catálogo para un retailer.
    /// 1. Sincroniza el árbol de categorías.
    /// 2. Barre todos los productos de cada categoría encontrada.
    /// </summary>
    public async Task<SweepSummary> SweepFullCatalogAsync(string host, int? salesChannel, CancellationToken ct)
    {
        var summary = new SweepSummary(host);
        _log.LogInformation("Iniciando barrido completo de catálogo para {Host}...", host);

        // Creamos un "scope" de servicios propio para esta tarea de larga duración.
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var syncService = scope.ServiceProvider.GetRequiredService<CatalogSyncService>();

        try
        {
            // --- PASO 1: Sincronizar el árbol de categorías ---
            _log.LogInformation("Sincronizando categorías para {Host}...", host);
            var categoryCount = await syncService.SyncCategoriesAsync(host, 50, ct);
            summary.CategoriesSynced = categoryCount;
            _log.LogInformation("Se sincronizaron {Count} categorías para {Host}.", categoryCount, host);

            // --- PASO 2: Obtener la lista de categorías de nuestra base de datos ---
            var categoryIds = await db.Categories
                                      .Where(c => c.RetailerHost == host) // Aseguramos que sean solo las de este host
                                      .AsNoTracking()
                                      .Select(c => c.CategoryId)
                                      .ToListAsync(ct);

            summary.TotalCategoriesToSweep = categoryIds.Count;
            _log.LogInformation("Se barrerán los productos de {Count} categorías.", categoryIds.Count);

            // --- PASO 3: Barrer los productos de cada categoría ---
            int processedCategories = 0;
            foreach (var categoryId in categoryIds)
            {
                if (ct.IsCancellationRequested)
                {
                    _log.LogWarning("La operación de barrido fue cancelada.");
                    break;
                }

                processedCategories++;
                _log.LogInformation("[{Processed}/{Total}] Iniciando barrido de productos para categoría {CategoryId}...",
                    processedCategories, categoryIds.Count, categoryId);

                try
                {
                    // --- ¡CORRECCIÓN CLAVE AQUÍ! ---
                    // Ahora llamamos al servicio correcto (CatalogSyncService) para que guarde los productos.
                    var (total, upserts) = await syncService.SyncProductsAsync(
                        host: host,
                        categoryId: categoryId,
                        pageSize: 50,
                        maxPages: null, // Dejamos que el servicio decida cuándo parar
                        ct: ct);
                    // --- FIN DE LA CORRECCIÓN ---

                    summary.TotalProductsFound += total;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Falló el barrido para la categoría {CategoryId} en {Host}.", categoryId, host);
                    summary.FailedCategories++;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falló el proceso de orquestación de barrido para {Host}.", host);
            summary.ErrorMessage = ex.Message;
        }

        _log.LogInformation("Barrido completo para {Host}. Total de productos procesados: {ProductsCount}",
            host, summary.TotalProductsFound);
        return summary;
    }
}

public sealed record SweepSummary(string Host)
{
    public int CategoriesSynced { get; set; }
    public int TotalCategoriesToSweep { get; set; }
    public int TotalProductsFound { get; set; }
    public int ProductsSkipped { get; set; }
    public int TotalRequests { get; set; }
    public int FailedCategories { get; set; }
    public string? ErrorMessage { get; set; }
}