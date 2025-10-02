// Ruta: ScrapeMart/Services/MasterOrchestratorService.cs

using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

/// <summary>
/// 🚀 ORQUESTADOR CORREGIDO: Orquesta el ciclo completo de scraping y sondeo.
/// ✅ Llama a servicios que SÍ usan Proxy y Cookies.
/// ✅ Usa el `ImprovedAvailabilityService` para el sondeo de disponibilidad.
/// </summary>
public sealed class MasterOrchestratorService(IServiceProvider serviceProvider, ILogger<MasterOrchestratorService> log)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<MasterOrchestratorService> _log = log;

    public async Task RunFullProcessAsync(string? specificHost = null)
    {
        _log.LogInformation("--- INICIANDO PROCESO DE ORQUESTACIÓN MAESTRA (VERSIÓN MEJORADA) ---");

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // 1. Obtener la lista de retailers a procesar
        var retailersToProcess = await db.VtexRetailersConfigs
            .Where(c => c.Enabled && (specificHost == null || c.RetailerHost == specificHost))
            .AsNoTracking()
            .ToListAsync();

        _log.LogInformation("Se procesarán {Count} retailers.", retailersToProcess.Count);

        foreach (var retailer in retailersToProcess)
        {
            var host = retailer.RetailerHost;
            _log.LogInformation("--- Iniciando ciclo para retailer: {Host} ---", host);

            try
            {
                // PASO 1: Descargar el Catálogo de Productos (usa un servicio que ya está preparado)
                _log.LogInformation("[{Host}] - PASO 1/3: Iniciando scraping de catálogo...", host);
                var brandScraper = scope.ServiceProvider.GetRequiredService<BrandScrapingService>();
                await brandScraper.ScrapeTrackedBrandsAsync(host, CancellationToken.None);
                _log.LogInformation("[{Host}] - PASO 1/3: Scraping de catálogo completado.", host);

                // PASO 2: Mapear Sucursales a Pick-up Points (usando el VtexSweepService corregido)
                _log.LogInformation("[{Host}] - PASO 2/3: Iniciando mapeo de sucursales (Sweep)...", host);
                var sweepService = scope.ServiceProvider.GetRequiredService<VtexSweepService>();
                await sweepService.SweepAsync(host, null, null, CancellationToken.None);
                _log.LogInformation("[{Host}] - PASO 2/3: Mapeo de sucursales completado.", host);

                // --- 👇 ACÁ ESTÁ EL CAMBIO MÁS IMPORTANTE 👇 ---
                // PASO 3: Sondear Disponibilidad usando el servicio MEJORADO
                _log.LogInformation("[{Host}] - PASO 3/3: Iniciando sondeo de disponibilidad con `ImprovedAvailabilityService`...", host);
                var improvedAvailabilityService = scope.ServiceProvider.GetRequiredService<ImprovedAvailabilityService>();
                await improvedAvailabilityService.RunComprehensiveCheckAsync(host, CancellationToken.None);
                _log.LogInformation("[{Host}] - PASO 3/3: Sondeo de disponibilidad mejorado completado.", host);
                // --- 👆 FIN DEL CAMBIO 👆 ---

                _log.LogInformation("--- Ciclo para {Host} finalizado exitosamente. ---", host);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "--- Falló el ciclo completo para el retailer {Host}. Continuando con el siguiente. ---", host);
            }
        }

        _log.LogInformation("--- PROCESO DE ORQUESTACIÓN MAESTRA FINALIZADO ---");
    }
}