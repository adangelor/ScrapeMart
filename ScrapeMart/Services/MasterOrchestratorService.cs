// Ruta: ScrapeMart/Services/MasterOrchestratorService.cs
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

/// <summary>
/// Orquesta el ciclo completo de scraping y sondeo para todos los retailers configurados.
/// </summary>
public sealed class MasterOrchestratorService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MasterOrchestratorService> _log;

    public MasterOrchestratorService(IServiceProvider serviceProvider, ILogger<MasterOrchestratorService> log)
    {
        _serviceProvider = serviceProvider;
        _log = log;
    }

    public async Task RunFullProcessAsync(string? specificHost = null)
    {
        _log.LogInformation("--- INICIANDO PROCESO DE ORQUESTACIÓN MAESTRA ---");

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // 1. Obtener la lista de retailers a procesar desde la configuración
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
                // PASO 1: Descargar el Catálogo de Productos por Marca/Prefijo
                _log.LogInformation("[{Host}] - PASO 1/3: Iniciando scraping por prefijo de marca...", host);
                var brandScraper = scope.ServiceProvider.GetRequiredService<BrandScrapingService>();
                await brandScraper.ScrapeTrackedBrandsAsync(host, CancellationToken.None);
                _log.LogInformation("[{Host}] - PASO 1/3: Scraping por prefijo de marca completado.", host);

                // PASO 2: Mapear Sucursales Físicas a Pick-up Points online
                _log.LogInformation("[{Host}] - PASO 2/3: Iniciando mapeo de sucursales (Sweep)...", host);
                var sweepService = scope.ServiceProvider.GetRequiredService<VtexSweepService>();
                await sweepService.SweepAsync(host, null, null, CancellationToken.None);
                _log.LogInformation("[{Host}] - PASO 2/3: Mapeo de sucursales completado.", host);

                // PASO 3: Sondear Disponibilidad de los productos trackeados
                _log.LogInformation("[{Host}] - PASO 3/3: Iniciando sondeo de disponibilidad...", host);
                var availabilityOrchestrator = scope.ServiceProvider.GetRequiredService<AvailabilityOrchestratorService>();
                // Usamos valores por defecto razonables para el sondeo
                await availabilityOrchestrator.ProbeEanListAsync(host, 20, 50, 8, CancellationToken.None);
                _log.LogInformation("[{Host}] - PASO 3/3: Sondeo de disponibilidad completado.", host);

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