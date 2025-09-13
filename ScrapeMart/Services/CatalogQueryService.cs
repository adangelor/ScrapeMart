using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
namespace ScrapeMart.Services
{
    public interface ICatalogQueryService
    {
        Task<ProductDto?> GetProductByIdAsync(int productId, CancellationToken ct = default);
        Task<SkuWithProductDto?> GetSkuByEanAsync(string ean, CancellationToken ct = default);
        Task<PagedResult<ProductListItemDto>> GetProductsByCategoryAsync(int categoryId, int page, int pageSize, CancellationToken ct = default);
        Task<IReadOnlyList<CategoryNodeDto>> GetCategoryBreadcrumbAsync(int categoryId, CancellationToken ct = default);
        Task<IReadOnlyList<OfferHistoryDto>> GetOfferHistoryBySkuAsync(int skuDbId, int take, CancellationToken ct = default);
        Task<PagedResult<ProductListItemDto>> SearchProductsAsync(string query, int page, int pageSize, CancellationToken ct = default);
    }

    public sealed class CatalogQueryService : ICatalogQueryService
    {
        private readonly AppDb _db;

        public CatalogQueryService(AppDb db) => _db = db;

        public async Task<ProductDto?> GetProductByIdAsync(int productId, CancellationToken ct = default)
        {
            var dto = await _db.Products.AsNoTracking()
                .Where(p => p.ProductId == productId)
                .Select(p => new ProductDto
                {
                    ProductDbId = p.Id,
                    ProductId = p.ProductId,
                    ProductName = p.ProductName,
                    Brand = p.Brand,
                    BrandId = p.BrandId,
                    LinkText = p.LinkText,
                    Link = p.Link,
                    ReleaseDateUtc = p.ReleaseDateUtc,
                    Categories = p.ProductCategories
                        .OrderBy(pc => pc.Category.CategoryId)
                        .Select(pc => new CategoryNodeDto
                        {
                            CategoryDbId = pc.Category.Id,
                            CategoryId = pc.Category.CategoryId,
                            Name = pc.Category.Name,
                            ParentId = pc.Category.ParentId
                        })
                        .ToList(),
                    Properties = p.Properties
                        .Select(pr => new PropertyDto { Name = pr.Name, Value = pr.Value })
                        .ToList(),
                    Skus = p.Skus
                        .Select(s => new SkuDto
                        {
                            SkuDbId = s.Id,
                            ItemId = s.ItemId,
                            Name = s.Name,
                            NameComplete = s.NameComplete,
                            Ean = s.Ean,
                            MeasurementUnit = s.MeasurementUnit,
                            UnitMultiplier = s.UnitMultiplier,
                            Images = s.Images
                                .Select(i => new ImageDto
                                {
                                    ImageId = i.ImageId,
                                    Label = i.Label,
                                    Url = i.Url,
                                    Alt = i.Alt
                                }).ToList(),
                            // IMPORTANTE: evitar .Where(o != null) dentro del árbol traducible.
                            // Usamos DefaultIfEmpty() para forzar LEFT JOIN y FirstOrDefault() sin filtro adicional.
                            Offers = s.Sellers
                                .Select(se => se.Offers
                                    .OrderByDescending(o => o.CapturedAtUtc)
                                    .Select(o => new OfferDto
                                    {
                                        SellerId = se.SellerId,
                                        SellerName = se.SellerName,
                                        SellerDefault = se.SellerDefault,
                                        Price = o.Price,
                                        ListPrice = o.ListPrice,
                                        SpotPrice = o.SpotPrice,
                                        PriceWithoutDiscount = o.PriceWithoutDiscount,
                                        AvailableQuantity = o.AvailableQuantity,
                                        PriceValidUntilUtc = o.PriceValidUntilUtc,
                                        CapturedAtUtc = o.CapturedAtUtc
                                    })
                                    .DefaultIfEmpty()
                                    .FirstOrDefault()
                                )
                                .ToList()
                        }).ToList()
                })
                .FirstOrDefaultAsync(ct);

            // Limpieza en memoria de posibles nulls en las colecciones anidadas
            if (dto != null)
            {
                foreach (var sku in dto.Skus)
                    sku.Offers = sku.Offers.Where(o => o is not null).ToList()!;
            }

            return dto;
        }

        public async Task<SkuWithProductDto?> GetSkuByEanAsync(string ean, CancellationToken ct = default)
        {
            var dto = await _db.Skus.AsNoTracking()
                .Where(s => s.Ean == ean)
                .Select(s => new SkuWithProductDto
                {
                    SkuDbId = s.Id,
                    ItemId = s.ItemId,
                    Ean = s.Ean!,
                    MeasurementUnit = s.MeasurementUnit,
                    UnitMultiplier = s.UnitMultiplier,
                    Product = new ProductHeadDto
                    {
                        ProductDbId = s.Product.Id,
                        ProductId = s.Product.ProductId,
                        ProductName = s.Product.ProductName,
                        Brand = s.Product.Brand,
                        BrandId = s.Product.BrandId
                    },
                    Images = s.Images.Select(i => new ImageDto
                    {
                        ImageId = i.ImageId,
                        Label = i.Label,
                        Url = i.Url,
                        Alt = i.Alt
                    }).ToList(),
                    // Última oferta por seller: OUTER APPLY con DefaultIfEmpty + FirstOrDefault traducible.
                    LatestOffers = s.Sellers
                        .Select(se => se.Offers
                            .OrderByDescending(o => o.CapturedAtUtc)
                            .Select(o => new OfferDto
                            {
                                SellerId = se.SellerId,
                                SellerName = se.SellerName,
                                SellerDefault = se.SellerDefault,
                                Price = o.Price,
                                ListPrice = o.ListPrice,
                                SpotPrice = o.SpotPrice,
                                PriceWithoutDiscount = o.PriceWithoutDiscount,
                                AvailableQuantity = o.AvailableQuantity,
                                PriceValidUntilUtc = o.PriceValidUntilUtc,
                                CapturedAtUtc = o.CapturedAtUtc
                            })
                            .DefaultIfEmpty()
                            .FirstOrDefault()
                        )
                        .ToList()
                })
                .FirstOrDefaultAsync(ct);

            if (dto != null)
                dto.LatestOffers = dto.LatestOffers.Where(o => o is not null).ToList()!;

            return dto;
        }

        public async Task<PagedResult<ProductListItemDto>> GetProductsByCategoryAsync(int categoryId, int page, int pageSize, CancellationToken ct = default)
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 200);

            var baseQuery = _db.ProductCategories.AsNoTracking()
                .Where(pc => pc.Category.CategoryId == categoryId)
                .Select(pc => pc.Product)
                .Distinct();

            var total = await baseQuery.CountAsync(ct);

            // Calcular precio mínimo tomando la última oferta por seller SIN usar Where sobre FirstOrDefault
            var items = await baseQuery
                .OrderBy(p => p.Brand).ThenBy(p => p.ProductName)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new ProductListItemDto
                {
                    ProductDbId = p.Id,
                    ProductId = p.ProductId,
                    ProductName = p.ProductName,
                    Brand = p.Brand,
                    BrandId = p.BrandId,
                    AnyEan = p.Skus.Select(s => s.Ean).FirstOrDefault(e => e != null)!,
                    MinLatestPrice =
                        p.Skus
                         .SelectMany(s => s.Sellers
                            .Select(se => se.Offers
                                .OrderByDescending(o => o.CapturedAtUtc)
                                .Select(o => (decimal?)o.Price)
                                .FirstOrDefault()))
                         .Min(),
                    HasStock =
                        p.Skus
                         .SelectMany(s => s.Sellers
                            .Select(se => se.Offers
                                .OrderByDescending(o => o.CapturedAtUtc)
                                .Select(o => (int?)o.AvailableQuantity)
                                .FirstOrDefault()))
                         .Any(q => q.HasValue && q.Value > 0)
                })
                .ToListAsync(ct);

            return new PagedResult<ProductListItemDto>
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = items
            };
        }

        public async Task<IReadOnlyList<CategoryNodeDto>> GetCategoryBreadcrumbAsync(int categoryId, CancellationToken ct = default)
        {
            var nodes = new List<CategoryNodeDto>();
            var current = await _db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.CategoryId == categoryId, ct);
            while (current != null)
            {
                nodes.Add(new CategoryNodeDto
                {
                    CategoryDbId = current.Id,
                    CategoryId = current.CategoryId,
                    Name = current.Name,
                    ParentId = current.ParentId
                });
                current = current.ParentDbId.HasValue
                    ? await _db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == current.ParentDbId.Value, ct)
                    : null;
            }
            nodes.Reverse();
            return nodes;
        }

        public async Task<IReadOnlyList<OfferHistoryDto>> GetOfferHistoryBySkuAsync(int skuDbId, int take, CancellationToken ct = default)
        {
            take = Math.Clamp(take, 1, 5000);
            return await _db.Offers.AsNoTracking()
                .Where(o => o.Seller.SkuDbId == skuDbId)
                .OrderByDescending(o => o.CapturedAtUtc)
                .Take(take)
                .Select(o => new OfferHistoryDto
                {
                    SellerId = o.Seller.SellerId,
                    SellerName = o.Seller.SellerName,
                    CapturedAtUtc = o.CapturedAtUtc,
                    Price = o.Price,
                    ListPrice = o.ListPrice,
                    SpotPrice = o.SpotPrice,
                    PriceWithoutDiscount = o.PriceWithoutDiscount,
                    AvailableQuantity = o.AvailableQuantity,
                    PriceValidUntilUtc = o.PriceValidUntilUtc
                })
                .ToListAsync(ct);
        }

        public async Task<PagedResult<ProductListItemDto>> SearchProductsAsync(string query, int page, int pageSize, CancellationToken ct = default)
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 200);
            query = query?.Trim() ?? string.Empty;

            var baseQuery = _db.Products.AsNoTracking()
                .Where(p =>
                    (p.ProductName != null && EF.Functions.Like(p.ProductName, $"%{query}%")) ||
                    (p.Brand != null && EF.Functions.Like(p.Brand, $"%{query}%")) ||
                    (p.ProductId != null && EF.Functions.Like(p.ProductId.ToString(), $"%{query}%")) ||
                    p.Skus.Any(s => s.Ean != null && s.Ean.Contains(query))
                );

            var total = await baseQuery.CountAsync(ct);

            var items = await baseQuery
                .OrderBy(p => p.Brand).ThenBy(p => p.ProductName)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new ProductListItemDto
                {
                    ProductDbId = p.Id,
                    ProductId = p.ProductId,
                    ProductName = p.ProductName,
                    Brand = p.Brand,
                    BrandId = p.BrandId,
                    AnyEan = p.Skus.Select(s => s.Ean).FirstOrDefault(e => e != null)!,
                    MinLatestPrice =
                        p.Skus
                         .SelectMany(s => s.Sellers
                            .Select(se => se.Offers
                                .OrderByDescending(o => o.CapturedAtUtc)
                                .Select(o => (decimal?)o.Price)
                                .FirstOrDefault()))
                         .Min(),
                    HasStock =
                        p.Skus
                         .SelectMany(s => s.Sellers
                            .Select(se => se.Offers
                                .OrderByDescending(o => o.CapturedAtUtc)
                                .Select(o => (int?)o.AvailableQuantity)
                                .FirstOrDefault()))
                         .Any(q => q.HasValue && q.Value > 0)
                })
                .ToListAsync(ct);

            return new PagedResult<ProductListItemDto>
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = items
            };
        }
    }

    public static class CatalogQueryServiceExtensions
    {
        public static IServiceCollection AddCatalogQueryService(this IServiceCollection services)
            => services.AddScoped<ICatalogQueryService, CatalogQueryService>();
    }

    public sealed class PagedResult<T>
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    }

    public sealed class ProductHeadDto
    {
        public int ProductDbId { get; set; }
        public int ProductId { get; set; } = default!;
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public int? BrandId { get; set; }
    }

    public sealed class ProductListItemDto
    {
        public int ProductDbId { get; set; }
        public int ProductId { get; set; } = default!;
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public int? BrandId { get; set; }
        public string? AnyEan { get; set; }
        public decimal? MinLatestPrice { get; set; }
        public bool HasStock { get; set; }
    }

    public sealed class ProductDto
    {
        public int ProductDbId { get; set; }
        public int ProductId { get; set; } = default!;
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public int? BrandId { get; set; }
        public string? LinkText { get; set; }
        public string? Link { get; set; }
        public DateTime? ReleaseDateUtc { get; set; }
        public List<CategoryNodeDto> Categories { get; set; } = new();
        public List<PropertyDto> Properties { get; set; } = new();
        public List<SkuDto> Skus { get; set; } = new();
    }

    public sealed class SkuDto
    {
        public int SkuDbId { get; set; }
        public string ItemId { get; set; } = default!;
        public string? Name { get; set; }
        public string? NameComplete { get; set; }
        public string? Ean { get; set; }
        public string? MeasurementUnit { get; set; }
        public decimal UnitMultiplier { get; set; }
        public List<ImageDto> Images { get; set; } = new();
        public List<OfferDto?> Offers { get; set; } = new();
    }

    public sealed class SkuWithProductDto
    {
        public int SkuDbId { get; set; }
        public string ItemId { get; set; } = default!;
        public string Ean { get; set; } = default!;
        public string? MeasurementUnit { get; set; }
        public decimal UnitMultiplier { get; set; }
        public ProductHeadDto Product { get; set; } = default!;
        public List<ImageDto> Images { get; set; } = new();
        public List<OfferDto?> LatestOffers { get; set; } = new();
    }

    public sealed class ImageDto
    {
        public string? ImageId { get; set; }
        public string? Label { get; set; }
        public string? Url { get; set; }
        public string? Alt { get; set; }
    }

    public sealed class OfferDto
    {
        public string SellerId { get; set; } = default!;
        public string? SellerName { get; set; }
        public bool SellerDefault { get; set; }
        public decimal Price { get; set; }
        public decimal ListPrice { get; set; }
        public decimal SpotPrice { get; set; }
        public decimal PriceWithoutDiscount { get; set; }
        public int AvailableQuantity { get; set; }
        public DateTime? PriceValidUntilUtc { get; set; }
        public DateTime CapturedAtUtc { get; set; }
    }

    public sealed class OfferHistoryDto
    {
        public string SellerId { get; set; } = default!;
        public string? SellerName { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public decimal Price { get; set; }
        public decimal ListPrice { get; set; }
        public decimal SpotPrice { get; set; }
        public decimal PriceWithoutDiscount { get; set; }
        public int AvailableQuantity { get; set; }
        public DateTime? PriceValidUntilUtc { get; set; }
    }

    public sealed class CategoryNodeDto
    {
        public int CategoryDbId { get; set; }
        public int CategoryId { get; set; }
        public string? Name { get; set; }
        public int? ParentId { get; set; }
    }

    public sealed class PropertyDto
    {
        [Required] public string Name { get; set; } = default!;
        [Required] public string Value { get; set; } = default!;
    }
}