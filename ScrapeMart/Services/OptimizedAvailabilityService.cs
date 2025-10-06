using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

/// <summary>
/// Servicio OPTIMIZADO que usa directamente la tabla Stores 
/// sin hacer discovery de pickup points (¡ya los tenemos!)
/// </summary>
public sealed class OptimizedAvailabilityService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OptimizedAvailabilityService> _log;
    private readonly string _sqlConn;

    public OptimizedAvailabilityService(
        IServiceProvider serviceProvider,
        ILogger<OptimizedAvailabilityService> log,
        IConfiguration cfg)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
    }

    /// <summary>
    /// Sondea disponibilidad de todos los EANs trackeados en todas las tiendas activas
    /// SIN hacer discovery (usa directamente tabla Stores)
    /// </summary>
    public async Task ProbeAllEansInAllStoresAsync(
        string host,
        int minBatchSize = 20,
        int maxBatchSize = 50,
        int degreeOfParallelism = 8,
        CancellationToken ct = default)
    {
        _log.LogInformation("🚀 INICIO SONDEO OPTIMIZADO para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        // 1️⃣ Obtener configuración del retailer
        var config = await db.VtexRetailersConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RetailerHost == host && c.Enabled, ct);

        if (config is null)
        {
            _log.LogError("❌ No se encontró configuración para {Host}", host);
            return;
        }

        var salesChannel = int.Parse(config.SalesChannels.Split(',').First());

        // 2️⃣ Obtener EANs a trackear
        var eansToProbe = await db.ProductsToTrack
            .AsNoTracking()
            .Select(p => p.EAN)
            .ToListAsync(ct);

        // 3️⃣ Obtener SKUs con sellers para esos EANs
        var skusWithSellers = await db.Sellers
            .AsNoTracking()
            .Where(sel => sel.Sku.RetailerHost == host &&
                         sel.Sku.Ean != null &&
                         eansToProbe.Contains(sel.Sku.Ean))
            .Select(sel => new SkuSellerPair
            {
                SkuId = sel.Sku.ItemId,
                SellerId = sel.SellerId,
                Ean = sel.Sku.Ean!
            })
            .Distinct()
            .ToListAsync(ct);
            var activeStores = await (
            from store in db.Stores.AsNoTracking()
            join retailer in db.Retailers.AsNoTracking() on store.RetailerId equals retailer.RetailerId
            where store.IsActive &&
                  store.VtexPickupPointId != null &&
                  store.PostalCode != null &&
                  retailer.IsActive &&
                  retailer.VtexHost == host // ¡Filtrar por el host específico!
            select new StoreInfo
            {
                PickupPointId = store.VtexPickupPointId!,
                PostalCode = store.PostalCode!,
                City = store.City,
                Province = store.Province,
                StoreId = store.StoreId,
                RetailerId = store.RetailerId
            }).ToListAsync(ct);

        _log.LogInformation("📊 DATOS CARGADOS:");
        _log.LogInformation("   • EANs a trackear: {EanCount}", eansToProbe.Count);
        _log.LogInformation("   • SKUs con sellers: {SkuCount}", skusWithSellers.Count);
        _log.LogInformation("   • Stores activas: {StoreCount}", activeStores.Count);
        _log.LogInformation("   • Total simulaciones: {Total}", skusWithSellers.Count * activeStores.Count);

        if (skusWithSellers.Count == 0 || activeStores.Count == 0)
        {
            _log.LogWarning("⚠️ No hay datos suficientes para procesar");
            return;
        }

        // 5️⃣ Procesar en paralelo por tienda
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = degreeOfParallelism,
            CancellationToken = ct
        };

        var random = new Random();
        var totalProcessed = 0;

        await Parallel.ForEachAsync(activeStores, parallelOptions, async (store, token) =>
        {
            var storeProcessed = 0;
            try
            {
                // Dividir SKUs en lotes aleatorios
                var batches = skusWithSellers
                    .Chunk(random.Next(minBatchSize, maxBatchSize + 1))
                    .ToList();

                foreach (var batch in batches)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        // 🎯 SIMULACIÓN DIRECTA - SIN DISCOVERY
                        var result = await SimulateBatchInStoreAsync(
                            httpFactory, host, salesChannel, batch, store, token);

                        // Persistir resultados
                        await PersistBatchResultsAsync(host, store, salesChannel, result, batch, token);

                        storeProcessed += batch.Length;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "❌ Error procesando batch en store {StoreId} ({City})",
                            store.StoreId, store.City);
                    }
                }

                Interlocked.Add(ref totalProcessed, storeProcessed);

                _log.LogInformation("✅ Store {StoreId} ({City}, {Province}) completada. Procesados: {Count}",
                    store.StoreId, store.City, store.Province, storeProcessed);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "❌ Error general en store {StoreId}", store.StoreId);
            }
        });

        _log.LogInformation("🎉 SONDEO OPTIMIZADO COMPLETADO para {Host}. Total procesado: {Total}",
            host, totalProcessed);
    }

    /// <summary>
    /// Simula un batch de SKUs en una tienda específica
    /// </summary>
    private async Task<VtexPublicClient.MultiSimResult> SimulateBatchInStoreAsync(
        IHttpClientFactory httpFactory,
        string host,
        int salesChannel,
        SkuSellerPair[] batch,
        StoreInfo store,
        CancellationToken ct)
    {
        var httpClient = httpFactory.CreateClient("vtexSession");
        var vtexClient = new VtexPublicClient();

        var skuIdentifiers = batch.Select(s => new VtexPublicClient.SkuIdentifier(s.SkuId, s.SellerId, 1));

        // 🔥 LLAMADA DIRECTA CON DATOS REALES - SIN DISCOVERY
        return await vtexClient.SimulateMultiSkuPickupAsync(
            httpClient,
            host,
            salesChannel,
            skuIdentifiers,
            "AR",
            store.PostalCode,
            store.PickupPointId,
            store.City,      // 🎯 Ciudad real
            store.Province,  // 🎯 Provincia real
            ct);
    }

    /// <summary>
    /// Persiste los resultados de un batch en la base de datos
    /// </summary>
    private async Task PersistBatchResultsAsync(
        string host,
        StoreInfo store,
        int salesChannel,
        VtexPublicClient.MultiSimResult result,
        SkuSellerPair[] batch,
        CancellationToken ct)
    {
        foreach (var skuSeller in batch)
        {
            try
            {
                bool available = false;
                decimal? price = null;
                decimal? listPrice = null;

                if (result.Items.TryGetValue(skuSeller.SkuId, out var simItem))
                {
                    available = simItem.Available;
                    price = simItem.Price;
                    listPrice = simItem.ListPrice;
                }

                await UpsertAvailabilityAsync(
                    host: host,
                    pickupPointId: store.PickupPointId,
                    skuId: skuSeller.SkuId,
                    sellerId: skuSeller.SellerId,
                    salesChannel: salesChannel,
                    countryCode: "AR",
                    postalCode: store.PostalCode,
                    available: available,
                    maxQty: available ? 999 : 0, // Simplificado para batch
                    price: price,
                    currency: "ARS",
                    rawJson: result.Raw,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "❌ Error persistiendo SKU {SkuId} en store {StoreId}",
                    skuSeller.SkuId, store.StoreId);
            }
        }
    }

    /// <summary>
    /// Guarda/actualiza disponibilidad en la base de datos
    /// </summary>
    private async Task UpsertAvailabilityAsync(
        string host, string pickupPointId, string skuId, string sellerId, int salesChannel,
        string countryCode, string postalCode, bool available, int maxQty, decimal? price,
        string currency, string rawJson, CancellationToken ct)
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
        cmd.Parameters.AddWithValue("@sc", salesChannel);
        cmd.Parameters.AddWithValue("@country", countryCode);
        cmd.Parameters.AddWithValue("@postal", postalCode);
        cmd.Parameters.AddWithValue("@avail", available);
        cmd.Parameters.AddWithValue("@maxQty", maxQty);
        cmd.Parameters.AddWithValue("@price", (object?)price ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@curr", currency);
        cmd.Parameters.AddWithValue("@raw", (object?)rawJson?.Substring(0, Math.Min(rawJson.Length, 4000)) ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// DTOs helper
public sealed record SkuSellerPair
{
    public string SkuId { get; init; } = default!;
    public string SellerId { get; init; } = default!;
    public string Ean { get; init; } = default!;
}

public sealed record StoreInfo
{
    public string PickupPointId { get; init; } = default!;
    public string PostalCode { get; init; } = default!;
    public string City { get; init; } = default!;
    public string Province { get; init; } = default!;
    public long StoreId { get; init; }
    public string RetailerId { get; init; } = default!;
    public string StoreName { get; internal set; }
    public string? VtexPickupPointId { get; internal set; }
    public double Longitude { get; internal set; }
    public double Latitude { get; internal set; }
    public string Street { get; internal set; }
    public string Number { get; internal set; }
}