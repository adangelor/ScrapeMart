using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScrapeMart.Entities;
using ScrapeMart.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScrapeMart.Services
{
    public sealed class CatalogSyncService
    {
        private readonly AppDb _db;
        private readonly VtexCatalogClient _client;
        private readonly VtexOptions _opt;

        public CatalogSyncService(AppDb db, VtexCatalogClient client, IOptions<VtexOptions> opt)
        {
            _db = db;
            _client = client;
            _opt = opt.Value;
        }

        public async Task<int> SyncCategoriesAsync()
        {
            var tree = await _client.GetCategoryTreeAsync(_opt.CategoryTreeDepth);
            var flat = new List<(int id, string name, int? parentId)>();
            void Walk(JsonArray arr, int? parent)
            {
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) continue;
                    var id = (int?)o["id"] ?? 0;
                    var name = o["name"]?.ToString() ?? "";
                    flat.Add((id, name, parent));
                    if (o["children"] is JsonArray ch) Walk(ch, id);
                }
            }
            Walk(tree, null);

            var map = await _db.Categories.ToDictionaryAsync(x => x.CategoryId, x => x);
            foreach (var (id, name, parentId) in flat)
            {
                if (!map.TryGetValue(id, out var cat))
                {
                    cat = new Category { CategoryId = id };
                    _db.Categories.Add(cat);
                    map[id] = cat;
                }
                cat.Name = name;
                cat.ParentId = parentId;
            }
            await _db.SaveChangesAsync();

            // Link parents
            var byId = await _db.Categories.ToDictionaryAsync(x => x.CategoryId, x => x);
            foreach (var c in byId.Values)
            {
                c.ParentDbId = c.ParentId.HasValue && byId.TryGetValue(c.ParentId.Value, out var p) ? p.Id : null;
            }
            await _db.SaveChangesAsync();
            return flat.Count;
        }

        public async Task<object> SyncProductsAsync(int? categoryId, int? maxPages)
        {
            var pageSize = _opt.PageSize;
            var totalProducts = 0;
            var upserts = 0;

            var categories = new List<int>();
            if (categoryId.HasValue) categories.Add(categoryId.Value);
            else
                categories = await _db.Categories.Select(c => c.CategoryId).ToListAsync();

            foreach (var cid in categories)
            {
                await foreach (var prod in _client.GetProductsByCategoryAsync(cid, pageSize, maxPages))
                {
                    var pid = prod["productId"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(pid)) continue;

                    var p = await _db.Products.Include(x => x.ProductCategories)
                                              .FirstOrDefaultAsync(x => x.ProductId == pid);
                    var isNew = false;
                    if (p is null)
                    {
                        p = new Product { ProductId = pid };
                        _db.Products.Add(p);
                        isNew = true;
                    }

                    p.ProductName = prod["productName"]?.ToString();
                    p.Brand = prod["brand"]?.ToString();
                    p.BrandId = TryInt(prod["brandId"]);
                    p.LinkText = prod["linkText"]?.ToString();
                    p.Link = prod["link"]?.ToString();
                    p.CacheId = prod["cacheId"]?.ToString();
                    p.ReleaseDateUtc = TryDate(prod["releaseDate"]);
                    p.RawJson = prod.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

                    var catIds = new HashSet<int>();
                    if (prod["categoriesIds"] is JsonArray cids)
                    {
                        foreach (var c in cids)
                            if (int.TryParse(c?.ToString()?.Trim('/'), out var val)) catIds.Add(val);
                    }
                    else if (prod["categories"] is JsonArray cats)
                    {
                        // fallback: parse from names not ids (skip if not usable)
                    }

                    // upsert product-category links
                    var existingLinks = p.ProductCategories.ToList();
                    foreach (var link in existingLinks)
                        _db.ProductCategories.Remove(link);
                    if (catIds.Count > 0)
                    {
                        var catMap = await _db.Categories.Where(x => catIds.Contains(x.CategoryId)).ToListAsync();
                        foreach (var c in catMap)
                            _db.ProductCategories.Add(new ProductCategory { Product = p, Category = c });
                    }

                    upserts += isNew ? 1 : 0;

                    // SKUs
                    if (prod["items"] is JsonArray items)
                    {
                        var skus = await _db.Skus.Where(s => s.ProductDbId == p.Id).ToListAsync();
                        var byItemId = skus.ToDictionary(s => s.ItemId, s => s);

                        foreach (var it in items.OfType<JsonObject>())
                        {
                            var itemId = it["itemId"]?.ToString();
                            if (string.IsNullOrEmpty(itemId)) continue;

                            if (!byItemId.TryGetValue(itemId, out var sku))
                            {
                                sku = new Sku { ItemId = itemId, Product = p };
                                _db.Skus.Add(sku);
                                byItemId[itemId] = sku;
                            }

                            sku.Name = it["name"]?.ToString();
                            sku.NameComplete = it["nameComplete"]?.ToString();
                            sku.Ean = it["ean"]?.ToString();
                            sku.MeasurementUnit = it["measurementUnit"]?.ToString();
                            sku.UnitMultiplier = TryDecimal(it["unitMultiplier"]) ?? 1m;

                            // Images
                            var imgsExisting = await _db.Images.Where(x => x.SkuDbId == sku.Id).ToListAsync();
                            foreach (var im in imgsExisting) _db.Images.Remove(im);
                            if (it["images"] is JsonArray imgs)
                            {
                                foreach (var im in imgs.OfType<JsonObject>())
                                {
                                    _db.Images.Add(new Image
                                    {
                                        Sku = sku,
                                        ImageId = im["imageId"]?.ToString(),
                                        Label = im["imageLabel"]?.ToString(),
                                        Url = im["imageUrl"]?.ToString(),
                                        Alt = im["imageText"]?.ToString()
                                    });
                                }
                            }

                            // Sellers + offers (si el JSON de búsqueda expone sellers; algunos tenants lo incluyen)
                            var sellersExisting = await _db.Sellers.Where(x => x.SkuDbId == sku.Id).ToListAsync();
                            foreach (var s in sellersExisting) _db.Sellers.Remove(s);

                            if (it["sellers"] is JsonArray sellers)
                            {
                                foreach (var s in sellers.OfType<JsonObject>())
                                {
                                    var seller = new Seller
                                    {
                                        Sku = sku,
                                        SellerId = s["sellerId"]?.ToString() ?? "1",
                                        SellerName = s["sellerName"]?.ToString(),
                                        SellerDefault = (bool?)s["sellerDefault"] ?? true
                                    };
                                    _db.Sellers.Add(seller);

                                    if (s["commertialOffer"] is JsonObject offer)
                                    {
                                        _db.Offers.Add(new CommercialOffer
                                        {
                                            Seller = seller,
                                            Price = TryDecimal(offer["Price"]) ?? 0m,
                                            ListPrice = TryDecimal(offer["ListPrice"]) ?? 0m,
                                            SpotPrice = TryDecimal(offer["spotPrice"]) ?? 0m,
                                            PriceWithoutDiscount = TryDecimal(offer["PriceWithoutDiscount"]) ?? 0m,
                                            PriceValidUntilUtc = TryDate(offer["PriceValidUntil"]),
                                            AvailableQuantity = TryInt(offer["AvailableQuantity"]) ?? 0,
                                            CapturedAtUtc = DateTime.UtcNow
                                        });
                                    }
                                }
                            }
                        }
                    }

                    // Properties (flatten simple properties list if available)
                    if (prod["properties"] is JsonArray props)
                    {
                        var existing = await _db.Properties.Where(x => x.ProductDbId == p.Id).ToListAsync();
                        foreach (var pr in existing) _db.Properties.Remove(pr);

                        foreach (var pr in props.OfType<JsonObject>())
                        {
                            var name = pr["name"]?.ToString();
                            if (string.IsNullOrEmpty(name)) continue;

                            if (pr["values"] is JsonArray vals)
                            {
                                foreach (var v in vals)
                                {
                                    _db.Properties.Add(new ProductProperty
                                    {
                                        Product = p,
                                        Name = name,
                                        Value = v?.ToString() ?? ""
                                    });
                                }
                            }
                        }
                    }

                    await _db.SaveChangesAsync();
                    totalProducts++;
                }
            }

            return new { totalProducts, upserts, pageSize };
        }

        private static int? TryInt(JsonNode? n)
            => n is null ? null : (int.TryParse(n.ToString(), out var i) ? i : null);

        private static decimal? TryDecimal(JsonNode? n)
            => n is null ? null : (decimal.TryParse(n.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null);

        private static DateTime? TryDate(JsonNode? n)
        {
            if (n is null) return null;
            if (DateTime.TryParse(n.ToString(), out var dt)) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            if (long.TryParse(n.ToString(), out var epoch))
                return DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime;
            return null;
        }
    }
}
