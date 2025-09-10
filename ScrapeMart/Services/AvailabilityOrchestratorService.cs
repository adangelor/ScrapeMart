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