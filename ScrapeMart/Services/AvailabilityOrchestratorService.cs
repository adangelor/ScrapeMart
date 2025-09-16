using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

public sealed class AvailabilityOrchestratorService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AvailabilityOrchestratorService> _log;
    private readonly string _sqlConn;
    public AvailabilityOrchestratorService(IServiceProvider serviceProvider, ILogger<AvailabilityOrchestratorService> log, IConfiguration cfg)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!; // Necesitamos la connection string para el guardado
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

        var eansToProbe = await db.ProductsToTrack.AsNoTracking().Select(p => p.EAN).ToListAsync(ct);
        var config = await db.VtexRetailersConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.RetailerHost == host && c.Enabled, ct);
        if (config is null) { _log.LogError("No se encontró config para {Host}", host); return; }
        var salesChannel = int.Parse(config.SalesChannels.Split(',').First());

        var skus = await db.Sellers.AsNoTracking()
            .Where(sel => sel.Sku.RetailerHost == host && sel.Sku.Ean != null && eansToProbe.Contains(sel.Sku.Ean))
            .Select(sel => new VtexPublicClient.SkuIdentifier(sel.Sku.ItemId, sel.SellerId, 1))
            .Distinct()
            .ToListAsync(ct);

        _log.LogInformation("De {EanCount} EANs, se encontraron {SkuCount} SKUs válidos y con vendedor en la DB para el host {Host}.", eansToProbe.Count, skus.Count, host);
        if (skus.Count == 0) return;

        var locations = await db.VtexPickupPoints.AsNoTracking()
            .Where(pp => pp.RetailerHost == host && pp.SourceIdSucursal != null)
            .Join(db.Sucursales.AsNoTracking(),
                  pp => new { B = pp.SourceIdBandera.Value, C = pp.SourceIdComercio.Value, S = pp.SourceIdSucursal.Value },
                  s => new { B = s.IdBandera, C = s.IdComercio, S = s.IdSucursal },
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

                    // --- ¡LA LÓGICA DE PERSISTENCIA QUE FALTABA! ---
                    await PersistBatchAvailabilityAsync(host, location.PickupPointId, salesChannel, location.SucursalesCodigoPostal, result, batch, token);

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

    // --- ¡NUEVO MÉTODO PRIVADO PARA GUARDAR LOS DATOS DEL BATCH! ---
    private async Task PersistBatchAvailabilityAsync(
        string host, string pickupPointId, int sc, string postalCode,
        VtexPublicClient.MultiSimResult result, IEnumerable<VtexPublicClient.SkuIdentifier> batch, CancellationToken ct)
    {
        foreach (var skuInBatch in batch)
        {
            // Buscamos el resultado para este SKU específico
            if (result.Items.TryGetValue(skuInBatch.Id, out var simItem))
            {
                // Si lo encontramos, lo guardamos
                await UpsertAvailabilityAsync(host, pickupPointId, skuInBatch.Id, skuInBatch.Seller, sc, "AR", postalCode,
                                              simItem.Available, 0, simItem.Price, "ARS", result.Raw, ct); // MaxFeasibleQty se omite en batch, guardamos 0
            }
            else
            {
                // Si la API no devolvió info para este SKU (raro, pero posible), lo marcamos como no disponible
                await UpsertAvailabilityAsync(host, pickupPointId, skuInBatch.Id, skuInBatch.Seller, sc, "AR", postalCode,
                                              false, 0, null, "ARS", result.Raw, ct);
            }
        }
    }

    // Método de guardado que usa SQL directo para ser más eficiente
    private async Task UpsertAvailabilityAsync(
        string host, string pickupPointId, string skuId, string sellerId, int sc,
        string country, string postal, bool available, int maxQty, decimal? price, string currency, string raw, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexStoreAvailability AS T
USING (VALUES(@host,@pp,@sku,@seller,@sc)) AS S (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel)
ON (T.RetailerHost=S.RetailerHost AND T.PickupPointId=S.PickupPointId AND T.SkuId=S.SkuId AND T.SellerId=S.SellerId AND T.SalesChannel=S.SalesChannel)
WHEN MATCHED THEN
  UPDATE SET IsAvailable=@avail, MaxFeasibleQty=@maxQty, Price=@price, Currency=@curr, CountryCode=@country, PostalCode=@postal, CapturedAtUtc=SYSUTCDATETIME(), RawJson=@raw
WHEN NOT MATCHED THEN
  INSERT (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel,CountryCode,PostalCode,IsAvailable,MaxFeasibleQty,Price,Currency,CapturedAtUtc,RawJson)
  VALUES (@host,@pp,@sku,@seller,@sc,@country,@postal,@avail,@maxQty,@price,@curr,SYSUTCDATETIME(),@raw);";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@pp", pickupPointId);
        cmd.Parameters.AddWithValue("@sku", skuId);
        cmd.Parameters.AddWithValue("@seller", sellerId);
        cmd.Parameters.AddWithValue("@sc", sc);
        cmd.Parameters.AddWithValue("@country", (object?)country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@postal", (object?)postal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@avail", available);
        cmd.Parameters.AddWithValue("@maxQty", maxQty);
        cmd.Parameters.AddWithValue("@price", (object?)price ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@curr", (object?)currency ?? DBNull.Value);
        // Guardamos solo una porción del JSON para no saturar la tabla
        cmd.Parameters.AddWithValue("@raw", (object?)raw.Substring(0, Math.Min(raw.Length, 4000)) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
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

