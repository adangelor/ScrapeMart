using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

/// <summary>
/// Transcribe la jerarquía completa (Producto -> SKU -> Seller -> Oferta)
/// desde las tablas VTEX de escenario hacia las tablas finales del modelo de datos.
/// </summary>
public sealed class VtexToProductsTranscriberService
{
    private readonly AppDb _db;
    private readonly string _sqlConn;
    private readonly ILogger<VtexToProductsTranscriberService> _log;

    public VtexToProductsTranscriberService(
        AppDb db,
        IConfiguration cfg,
        ILogger<VtexToProductsTranscriberService> log)
    {
        _db = db;
        _sqlConn = cfg.GetConnectionString("Default")!;
        _log = log;
    }

    public async Task<TranscriptionResult> TranscribeAllAsync(
        string host,
        int batchSize = 100,
        CancellationToken ct = default)
    {
        var result = new TranscriptionResult { Host = host };
        _log.LogInformation("Iniciando transcripción COMPLETA para host: {Host}", host);

        try
        {
            await using var connection = new SqlConnection(_sqlConn);
            var productIdsToProcess = (await connection.QueryAsync<int>(
                "SELECT ProductId FROM dbo.VtexProducts WHERE RetailerHost = @host",
                new { host })).AsList();

            result.TotalVtexProducts = productIdsToProcess.Count();
            _log.LogInformation("Encontrados {Count} productos en VtexProducts para transcribir", result.TotalVtexProducts);

            if (!productIdsToProcess.Any())
            {
                result.MarkCompleted();
                return result;
            }

            for (int i = 0; i < result.TotalVtexProducts; i += batchSize)
            {
                var batchIds = productIdsToProcess.Skip(i).Take(batchSize).ToList();
                if (!batchIds.Any()) continue; // Control por si las dudas

                await ProcessBatchAsync(host, batchIds, result, ct);

                _log.LogInformation("Procesado lote {Current}/{Total}",
                    Math.Min(i + batchSize, result.TotalVtexProducts), result.TotalVtexProducts);

                if (ct.IsCancellationRequested)
                {
                    _log.LogWarning("Transcripción cancelada por el usuario");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error fatal durante la transcripción para host: {Host}", host);
            result.ErrorMessage = ex.Message;
        }

        result.MarkCompleted();
        _log.LogInformation("Transcripción completada para {Host}. Procesados: {Processed}, Nuevos: {New}, Actualizados: {Updated}, Errores: {Errors}",
            host, result.ProductsProcessed, result.ProductsInserted, result.ProductsUpdated, result.ProductsWithErrors);

        return result;
    }

    private async Task ProcessBatchAsync(
        string host,
        List<int> productIds,
        TranscriptionResult result,
        CancellationToken ct)
    {
        var rawProducts = await GetRawProductDataForBatchAsync(host, productIds, ct);

        var existingProducts = await _db.Products
            .Include(p => p.Skus)
                .ThenInclude(s => s.Sellers)
            .Where(p => p.RetailerHost == host && productIds.Contains(p.ProductId))
            .ToDictionaryAsync(p => p.ProductId, p => p, ct);

        foreach (var rawProduct in rawProducts)
        {
            try
            {
                var isNewProduct = !existingProducts.TryGetValue(rawProduct.ProductId, out var finalProduct);
                if (isNewProduct)
                {
                    finalProduct = new Product
                    {
                        ProductId = rawProduct.ProductId,
                        RetailerHost = host,
                        ReleaseDateUtc = rawProduct.FirstSeenUtc
                    };
                    _db.Products.Add(finalProduct);
                    result.ProductsInserted++;
                }
                else
                {
                    result.ProductsUpdated++;
                }

                finalProduct.ProductName = rawProduct.ProductName;
                finalProduct.Brand = rawProduct.Brand;
                finalProduct.LinkText = rawProduct.LinkText;

                foreach (var rawSku in rawProduct.Skus)
                {
                    var finalSku = finalProduct.Skus.FirstOrDefault(s => s.ItemId == rawSku.SkuId.ToString());
                    if (finalSku is null)
                    {
                        finalSku = new Sku
                        {
                            ItemId = rawSku.SkuId.ToString(),
                            Product = finalProduct,
                            RetailerHost = host
                        };
                        finalProduct.Skus.Add(finalSku);
                    }

                    finalSku.Name = rawSku.SkuName;
                    finalSku.Ean = rawSku.Ean;
                    finalSku.MeasurementUnit = rawSku.MeasurementUnit;
                    finalSku.UnitMultiplier = rawSku.UnitMultiplier ?? 1m;

                    foreach (var rawSeller in rawSku.Sellers)
                    {
                        var finalSeller = finalSku.Sellers.FirstOrDefault(s => s.SellerId == rawSeller.SellerId);
                        if (finalSeller is null)
                        {
                            finalSeller = new Seller
                            {
                                SellerId = rawSeller.SellerId,
                                Sku = finalSku
                            };
                            finalSku.Sellers.Add(finalSeller);
                        }

                        finalSeller.SellerName = rawSeller.SellerName;
                        finalSeller.SellerDefault = rawSeller.SellerDefault;

                        if (rawSeller.LatestOffer is not null)
                        {
                            var offer = new CommercialOffer
                            {
                                Seller = finalSeller,
                                Price = rawSeller.LatestOffer.Price ?? 0m,
                                ListPrice = rawSeller.LatestOffer.ListPrice ?? 0m,
                                PriceWithoutDiscount = rawSeller.LatestOffer.PriceWithoutDiscount ?? 0m,
                                AvailableQuantity = rawSeller.LatestOffer.AvailableQuantity ?? 0,
                                PriceValidUntilUtc = rawSeller.LatestOffer.PriceValidUntilUtc,
                                CapturedAtUtc = rawSeller.LatestOffer.CapturedAtUtc
                            };
                            _db.Offers.Add(offer);
                        }
                    }
                }
                result.ProductsProcessed++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error procesando producto {ProductId} de host {Host}", rawProduct.ProductId, host);
                result.ProductsWithErrors++;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<List<RawProductDto>> GetRawProductDataForBatchAsync(string host, List<int> productIds, CancellationToken ct)
    {
        // --- ESTA ES LA CONSULTA CORREGIDA Y COMPLETA ---
        const string sql = @"
WITH LastOffer AS (
    SELECT
        RetailerHost, SkuId, SellerId, Price, ListPrice, PriceWithoutDiscount, AvailableQuantity, PriceValidUntilUtc, CapturedAtUtc,
        ROW_NUMBER() OVER(PARTITION BY RetailerHost, SkuId, SellerId ORDER BY CapturedAtUtc DESC) as rn
    FROM dbo.VtexOffers
)
SELECT
    p.ProductId, p.ProductName, p.Brand, p.LinkText, p.FirstSeenUtc,
    s.SkuId, s.SkuName, s.Ean, s.MeasurementUnit, s.UnitMultiplier,
    sel.SellerId, sel.SellerName, sel.SellerDefault,
    o.Price, o.ListPrice, o.PriceWithoutDiscount, o.AvailableQuantity, o.PriceValidUntilUtc, o.CapturedAtUtc
FROM dbo.VtexProducts p
JOIN dbo.VtexSkus s ON p.RetailerHost = s.RetailerHost AND p.ProductId = s.ProductId
JOIN dbo.VtexSkuSellers sel ON s.RetailerHost = sel.RetailerHost AND s.SkuId = sel.SkuId
LEFT JOIN LastOffer o ON sel.RetailerHost = o.RetailerHost AND sel.SkuId = o.SkuId AND sel.SellerId = o.SellerId AND o.rn = 1
WHERE p.RetailerHost = @host AND p.ProductId IN @productIds
ORDER BY p.ProductId, s.SkuId, sel.SellerId;";

        await using var connection = new SqlConnection(_sqlConn);
        var rawData = await connection.QueryAsync<dynamic>(sql, new { host, productIds });

        var groupedData = rawData.GroupBy(r => (int)r.ProductId)
            .Select(pg => new RawProductDto
            {
                ProductId = pg.Key,
                ProductName = pg.First().ProductName,
                Brand = pg.First().Brand,
                LinkText = pg.First().LinkText,
                FirstSeenUtc = pg.First().FirstSeenUtc,
                Skus = pg.GroupBy(s => (int)s.SkuId).Select(sg => new RawSkuDto
                {
                    SkuId = sg.Key,
                    SkuName = sg.First().SkuName,
                    Ean = sg.First().Ean,
                    MeasurementUnit = sg.First().MeasurementUnit,
                    UnitMultiplier = sg.First().UnitMultiplier,
                    Sellers = sg.GroupBy(sel => (string)sel.SellerId).Select(selg => new RawSellerDto
                    {
                        SellerId = selg.Key,
                        SellerName = selg.First().SellerName,
                        SellerDefault = selg.First().SellerDefault,
                        LatestOffer = selg.First().Price is null ? null : new RawOfferDto
                        {
                            Price = selg.First().Price,
                            ListPrice = selg.First().ListPrice,
                            PriceWithoutDiscount = selg.First().PriceWithoutDiscount,
                            AvailableQuantity = selg.First().AvailableQuantity,
                            PriceValidUntilUtc = selg.First().PriceValidUntilUtc,
                            CapturedAtUtc = selg.First().CapturedAtUtc
                        }
                    }).ToList()
                }).ToList()
            }).ToList();

        return groupedData;
    }

    // DTOs
    private sealed class RawProductDto { public int ProductId { get; set; } public string? ProductName { get; set; } public string? Brand { get; set; } public string? LinkText { get; set; } public DateTime? FirstSeenUtc { get; set; } public List<RawSkuDto> Skus { get; set; } = new(); }
    private sealed class RawSkuDto { public int SkuId { get; set; } public string? SkuName { get; set; } public string? Ean { get; set; } public string? MeasurementUnit { get; set; } public decimal? UnitMultiplier { get; set; } public List<RawSellerDto> Sellers { get; set; } = new(); }
    private sealed class RawSellerDto { public string SellerId { get; set; } = ""; public string? SellerName { get; set; } public bool SellerDefault { get; set; } public RawOfferDto? LatestOffer { get; set; } }
    private sealed class RawOfferDto { public decimal? Price { get; set; } public decimal? ListPrice { get; set; } public decimal? PriceWithoutDiscount { get; set; } public int? AvailableQuantity { get; set; } public DateTime? PriceValidUntilUtc { get; set; } public DateTime CapturedAtUtc { get; set; } }

    public sealed class TranscriptionResult
    {
        public string Host { get; set; } = default!;
        public int TotalVtexProducts { get; set; }
        public int ProductsProcessed { get; set; }
        public int ProductsInserted { get; set; }
        public int ProductsUpdated { get; set; }
        public int ProductsWithErrors { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt - StartedAt : null;
        public void MarkCompleted() => CompletedAt = DateTime.UtcNow;
    }
}