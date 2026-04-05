using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities.dtos;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

public interface IPriceQueryService
{
    Task<PriceQueryResponseDto?> GetPricesByEanAsync(string ean, string retailerHost, string storeId, CancellationToken ct = default);
}

public sealed class PriceQueryService : IPriceQueryService
{
    private readonly AppDb _db;

    public PriceQueryService(AppDb db) => _db = db;

    public async Task<PriceQueryResponseDto?> GetPricesByEanAsync(string ean, string retailerHost, string storeId, CancellationToken ct = default)
    {
        // Primero buscamos si existe el producto con ese EAN
        var skuInfo = await _db.Skus.AsNoTracking()
            .Where(s => s.Ean == ean && s.RetailerHost == retailerHost)
            .Select(s => new
            {
                SkuId = s.Id,
                ProductName = s.Product.ProductName,
                Brand = s.Product.Brand
            })
            .FirstOrDefaultAsync(ct);

        if (skuInfo == null)
            return null;

        // Buscamos información de la tienda y retailer
        var storeInfo = await _db.Stores.AsNoTracking()
            .Include(s => s.Retailer)
            .Where(s => s.StoreId == storeId && s.Retailer.VtexHost == retailerHost)
            .Select(s => new
            {
                StoreName = s.Name,
                RetailerName = s.Retailer.DisplayName
            })
            .FirstOrDefaultAsync(ct);

        if (storeInfo == null)
            return null;

        // Buscamos los precios más recientes desde AvailabilityResults
        var availabilityResults = await _db.AvailabilityResults.AsNoTracking()
            .Where(ar => ar.ProductEAN == ean && 
                        ar.RetailerHost == retailerHost && 
                        ar.StoreId == storeId)
            .OrderByDescending(ar => ar.CheckedAt)
            .Take(10) // Limitamos a los 10 más recientes
            .ToListAsync(ct);

        // Si no hay resultados de disponibilidad, buscamos las ofertas más recientes del SKU
        var prices = new List<PriceOfferDto>();
        
        if (availabilityResults.Any())
        {
            // Usamos los datos de AvailabilityResults
            var sellerIds = availabilityResults.Select(ar => ar.SellerId).Distinct().ToList();
            
            var sellerNames = await _db.Sellers.AsNoTracking()
                .Where(s => sellerIds.Contains(s.SellerId) && s.SkuDbId == skuInfo.SkuId)
                .ToDictionaryAsync(s => s.SellerId, s => s.SellerName, ct);

            prices = availabilityResults
                .GroupBy(ar => ar.SellerId)
                .Select(g => g.OrderByDescending(ar => ar.CheckedAt).First())
                .Select(ar => new PriceOfferDto(
                    SellerId: ar.SellerId,
                    SellerName: sellerNames.GetValueOrDefault(ar.SellerId) ?? ar.SellerId,
                    Price: ar.Price ?? 0,
                    ListPrice: null, // AvailabilityResults no tiene ListPrice
                    IsAvailable: ar.IsAvailable,
                    AvailableQuantity: ar.IsAvailable ? 1 : 0, // Aproximación
                    CapturedAt: ar.CheckedAt
                ))
                .ToList();
        }
        else
        {
            // Fallback: buscamos las ofertas más recientes del SKU
            var latestOffers = await _db.Sellers.AsNoTracking()
                .Where(s => s.SkuDbId == skuInfo.SkuId)
                .Select(s => s.Offers
                    .OrderByDescending(o => o.CapturedAtUtc)
                    .Select(o => new PriceOfferDto(
                        SellerId: s.SellerId,
                        SellerName: s.SellerName ?? s.SellerId,
                        Price: o.Price,
                        ListPrice: o.ListPrice,
                        IsAvailable: o.AvailableQuantity > 0,
                        AvailableQuantity: o.AvailableQuantity,
                        CapturedAt: o.CapturedAtUtc
                    ))
                    .FirstOrDefault())
                .Where(o => o != null)
                .ToListAsync(ct);

            prices = latestOffers!;
        }

        return new PriceQueryResponseDto(
            Ean: ean,
            ProductName: $"{skuInfo.Brand} {skuInfo.ProductName}".Trim(),
            StoreName: storeInfo.StoreName ?? storeId,
            RetailerName: storeInfo.RetailerName ?? retailerHost,
            Prices: prices
        );
    }
}

public static class PriceQueryServiceExtensions
{
    public static IServiceCollection AddPriceQueryService(this IServiceCollection services)
        => services.AddScoped<IPriceQueryService, PriceQueryService>();
}