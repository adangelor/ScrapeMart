using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

public sealed class AvailabilityOrchestratorService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AvailabilityOrchestratorService> _log;

    public AvailabilityOrchestratorService(
        IServiceProvider serviceProvider,
        ILogger<AvailabilityOrchestratorService> log)
    {
        _serviceProvider = serviceProvider;
        _log = log;
    }

    public async Task ProbeAllAsync(string host, CancellationToken ct)
    {
        _log.LogInformation("Iniciando sondeo masivo de disponibilidad para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var probeService = scope.ServiceProvider.GetRequiredService<VtexAvailabilityProbeService>();

        // --- CONSULTA CORREGIDA CON EL JOIN CORRECTO ---
        var workload = await (
            from sku in db.Skus
            join product in db.Products on sku.ProductDbId equals product.Id
            join seller in db.Sellers on sku.Id equals seller.SkuDbId
            join pickupPoint in db.VtexPickupPoints on product.RetailerHost equals pickupPoint.RetailerHost
            join sucursal in db.Sucursales
                on new { IdSucursal = pickupPoint.SourceIdSucursal.Value, IdComercio = pickupPoint.SourceIdComercio.Value, IdBandera = pickupPoint.SourceIdBandera.Value }
                equals new { sucursal.IdSucursal, sucursal.IdComercio, sucursal.IdBandera }
            join config in db.VtexRetailersConfigs on pickupPoint.RetailerHost equals config.RetailerHost
            where product.RetailerHost == host
            select new ProbeWorkItem
            {
                SkuId = sku.ItemId,
                SellerId = seller.SellerId,
                PickupPointId = pickupPoint.PickupPointId,
                PostalCode = sucursal.SucursalesCodigoPostal,
                SalesChannels = config.SalesChannels,
                Host = host,
                IdBandera = sucursal.IdBandera,
                IdComercio = sucursal.IdComercio
            }
        ).Distinct().ToListAsync(ct);
        // --- FIN DE LA CORRECCIÓN ---

        _log.LogInformation("Se encontraron {Count} combinaciones de SKU/Sucursal para sondear.", workload.Count);

        int count = 0;
        foreach (var item in workload)
        {
            if (ct.IsCancellationRequested)
            {
                _log.LogWarning("Sondeo masivo cancelado.");
                break;
            }

            count++;
            _log.LogInformation("[{Current}/{Total}] Sondeando SKU {SkuId} en PickupPoint {PickupPointId}...", count, workload.Count, item.SkuId, item.PickupPointId);

            var firstSalesChannel = int.Parse(item.SalesChannels.Split(',').First());

            try
            {
                await probeService.ProbePickupAsync(
                    host: item.Host,
                    salesChannel: firstSalesChannel,
                    skuId: item.SkuId,
                    sellerId: item.SellerId,
                    pickupPointId: item.PickupPointId,
                    countryCode: "AR",
                    postalCode: item.PostalCode,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falló el sondeo para SKU {SkuId} en PickupPoint {PickupPointId}", item.SkuId, item.PickupPointId);
            }
        }

        _log.LogInformation("Sondeo masivo para {Host} finalizado.", host);
    }

    // adangelor/scrapemart/ScrapeMart-307ccdf067def25afed98dbbac22027aa38c1af5/ScrapeMart/Services/AvailabilityOrchestratorService.cs

    public async Task ProbeEanListAsync(
        string host,
        int minBatchSize,
        int maxBatchSize,
        int degreeOfParallelism,
        CancellationToken ct)
    {
        _log.LogInformation("Iniciando sondeo por lista de EANs para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var vtexClient = new VtexPublicClient(scope.ServiceProvider.GetRequiredService<IHttpClientFactory>());

        var eansToProbe = await db.ProductsToTrack.AsNoTracking()
            .Select(s => s.EAN)
            .Distinct()
            .ToListAsync(ct);

        var config = await db.VtexRetailersConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.RetailerHost == host && c.Enabled, ct);
        if (config is null) { _log.LogError("No se encontró config para {Host}", host); return; }
        var salesChannel = int.Parse(config.SalesChannels.Split(',').First());

        // --- ¡CORRECCIÓN DEFINITIVA! ---
        // Partimos de la tabla Sellers para garantizar que cada SKU tenga un vendedor.
        // Esto es más seguro y eficiente.
        var skus = await db.Sellers.AsNoTracking()
            .Where(sel =>
                sel.Sku.RetailerHost == host &&
                sel.Sku.Ean != null &&
                eansToProbe.Contains(sel.Sku.Ean))
            .Select(sel => new VtexPublicClient.SkuIdentifier(sel.Sku.ItemId, sel.SellerId, 1))
            .Distinct()
            .ToListAsync(ct);

        _log.LogInformation("De {EanCount} EANs, se encontraron {SkuCount} SKUs válidos y con vendedor en la DB para el host {Host}.", eansToProbe.Count, skus.Count, host);

        if (skus.Count == 0)
        {
            _log.LogWarning("No se encontraron SKUs en la base de datos que coincidan con la lista de EANs para el host especificado. Asegúrate de que el catálogo de {Host} haya sido scrapeado.", host);
            return;
        }

        // El resto del método queda exactamente igual...
        var locations = await db.VtexPickupPoints.AsNoTracking()
            .Where(pp => pp.RetailerHost == host && pp.SourceIdSucursal != null)
            .Join(db.Sucursales.AsNoTracking(),
                  pp => new { Ids = pp.SourceIdSucursal.Value, Idc = pp.SourceIdComercio.Value, Idb = pp.SourceIdBandera.Value },
                  s => new { Ids = s.IdSucursal, Idc = s.IdComercio, Idb = s.IdBandera },
                  (pp, s) => new { pp.PickupPointId, s.SucursalesCodigoPostal })
            .Distinct().ToListAsync(ct);
        _log.LogInformation("Sondeando contra {LocationCount} sucursales.", locations.Count);

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism, CancellationToken = ct };
        var random = new Random();

        await Parallel.ForEachAsync(locations, parallelOptions, async (location, token) =>
        {
            var skuBatches = skus.Chunk(random.Next(minBatchSize, maxBatchSize + 1));

            foreach (var batch in skuBatches)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("vtexSession");
                    var result = await vtexClient.SimulateMultiSkuPickupAsync(httpClient, host, salesChannel, batch, "AR", location.SucursalesCodigoPostal, location.PickupPointId, token);
                    _log.LogInformation("Batch para sucursal {PickupId} procesado. {SuccessCount} SKUs con respuesta.", location.PickupPointId, result.Items.Count);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Falló un batch para la sucursal {PickupId}", location.PickupPointId);
                }
            }
        });

        _log.LogInformation("Sondeo por lista para {Host} finalizado.", host);
    }
}

// La clase auxiliar queda igual
file sealed record ProbeWorkItem
{
    public string SkuId { get; init; } = default!;
    public string SellerId { get; init; } = default!;
    public string PickupPointId { get; init; } = default!;
    public string PostalCode { get; init; } = default!;
    public string SalesChannels { get; init; } = default!;
    public string Host { get; init; } = default!;
    public int IdBandera { get; init; }
    public int IdComercio { get; init; }
}

