using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities;
using ScrapeMart.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScrapeMart.Services
{
    public sealed class CatalogSyncService(AppDb db, VtexCatalogClient client, ILogger<CatalogSyncService> log)
    {
        
        private readonly AppDb _db = db;
        private readonly VtexCatalogClient _client = client;
        private readonly ILogger<CatalogSyncService> _log = log;

        public async Task<int> SyncCategoriesAsync(string host, int categoryTreeDepth, CancellationToken ct)
        {
            var tree = await _client.GetCategoryTreeAsync(host, categoryTreeDepth, ct);
            var flat = new List<(int id, string name, int? parentId)>();
            void Walk(JsonArray arr, int? parent)
            {
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) continue;
                    var id = (int?)o["id"] ?? 0;
                    var name = o["name"]?.ToString() ?? "";
                    if (id == 0) continue;
                    flat.Add((id, name, parent));
                    if (o["children"] is JsonArray ch) Walk(ch, id);
                }
            }
            Walk(tree, null);

            var map = await _db.Categories.Where(c => c.RetailerHost == host).ToDictionaryAsync(x => x.CategoryId, x => x, ct);
            foreach (var (id, name, parentId) in flat)
            {
                if (!map.TryGetValue(id, out var cat))
                {
                    // Si es nueva, la creamos CON SU HOST
                    cat = new Category { CategoryId = id, RetailerHost = host };
                    _db.Categories.Add(cat);
                    map[id] = cat;
                }
                cat.Name = name;
                cat.ParentId = parentId;
            }
            await _db.SaveChangesAsync(ct);

            // Linkeamos los padres, de nuevo, solo para este host
            var byId = await _db.Categories.Where(c => c.RetailerHost == host).ToDictionaryAsync(x => x.CategoryId, x => x, ct);
            foreach (var c in byId.Values)
            {
                c.ParentDbId = c.ParentId.HasValue && byId.TryGetValue(c.ParentId.Value, out var p) ? p.Id : null;
            }
            await _db.SaveChangesAsync(ct);
            return flat.Count;
        }

           public async Task<(int total, int upserts)> SyncProductsAsync(string host, int? categoryId, int pageSize, int? maxPages, CancellationToken ct)
        {
            var totalProducts = 0;
            var upserts = 0;

            var categories = new List<int>();
            if (categoryId.HasValue)
            {
                categories.Add(categoryId.Value);
            }
            else
            {
                       categories = await _db.Categories.Where(c => c.RetailerHost == host).Select(c => c.CategoryId).ToListAsync(ct);
            }

            foreach (var cid in categories)
            {
                await foreach (var prod in _client.GetProductsByCategoryAsync(host, cid, pageSize, maxPages, ct))
                {
                    if(prod["productId"] == null || !int.TryParse(prod["productId"].ToString(), out int pid))
                    continue;

                    var p = await _db.Products.Include(x => x.ProductCategories)
                                              .FirstOrDefaultAsync(x => x.RetailerHost == host && x.ProductId == pid, ct);
                    var isNew = false;
                    if (p is null)
                    {
                        p = new Product { ProductId = pid, RetailerHost = host };
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

                    var existingLinks = p.ProductCategories.ToList();
                    foreach (var link in existingLinks) _db.ProductCategories.Remove(link);
                    if (catIds.Count > 0)
                    {
                        var catMap = await _db.Categories.Where(x => x.RetailerHost == host && catIds.Contains(x.CategoryId)).ToListAsync(ct);
                        foreach (var c in catMap)
                            _db.ProductCategories.Add(new ProductCategory { Product = p, Category = c });
                    }

                    upserts += isNew ? 1 : 0;

                    if (prod["items"] is JsonArray items)
                    {
                        var itemIdsInPayload = items.OfType<JsonObject>()
                                                    .Select(it => it["itemId"]?.ToString())
                                                    .Where(id => !string.IsNullOrEmpty(id))
                                                    .ToList();

                        // La búsqueda ahora es más específica y rápida
                        var byItemId = await _db.Skus
                            .Where(s => s.RetailerHost == host && itemIdsInPayload.Contains(s.ItemId))
                            .ToDictionaryAsync(s => s.ItemId, s => s, ct);

                        foreach (var it in items.OfType<JsonObject>())
                        {
                            var itemId = it["itemId"]?.ToString();
                            if (string.IsNullOrEmpty(itemId)) continue;

                            if (!byItemId.TryGetValue(itemId, out var sku))
                            {
                                sku = new Sku
                                {
                                    ItemId = itemId,
                                    Product = p,
                                    RetailerHost = host // --- ¡AQUÍ ESTÁ LA MAGIA! --- Se asigna el host
                                };
                                _db.Skus.Add(sku);
                                byItemId[itemId] = sku;
                            }

                            // El resto del mapeo queda igual
                            sku.Name = it["name"]?.ToString();
                            sku.NameComplete = it["nameComplete"]?.ToString();
                            sku.Ean = it["ean"]?.ToString();
                            sku.MeasurementUnit = it["measurementUnit"]?.ToString();
                            sku.UnitMultiplier = TryDecimal(it["unitMultiplier"]) ?? 1m;
                        }
                    }
                    await _db.SaveChangesAsync(ct);
                    totalProducts++;
                }
            }
            return (totalProducts, upserts);
        }

        private static int? TryInt(JsonNode? n) => n is null ? null : (int.TryParse(n.ToString(), out var i) ? i : null);
        private static decimal? TryDecimal(JsonNode? n) => n is null ? null : (decimal.TryParse(n.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null);
        private static DateTime? TryDate(JsonNode? n)
        {
            if (n is null) return null;
            if (DateTime.TryParse(n.ToString(), out var dt)) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            if (long.TryParse(n.ToString(), out var epoch))
                return DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime;
            return null;
        }

        public async Task<(int total, int upserts)> ProcessSingleProductNodeAsync(string host, JsonObject prod, CancellationToken ct)
        {
            if(!int.TryParse(prod["productId"]?.ToString() ?? "", out int pid))
                return (0, 0);

            var p = await _db.Products.Include(x => x.Skus).ThenInclude(s => s.Sellers).FirstOrDefaultAsync(x => x.RetailerHost == host && x.ProductId == pid, ct);

            var isNew = false;
            if (p is null)
            {
                p = new Product { ProductId = pid, RetailerHost = host };
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

            // ... (Lógica de categorías que ya funcionaba) ...

            if (prod["items"] is JsonArray items)
            {
                var byItemId = p.Skus.ToDictionary(s => s.ItemId, s => s);

                foreach (var it in items.OfType<JsonObject>())
                {
                    var itemId = it["itemId"]?.ToString();
                    if (string.IsNullOrEmpty(itemId)) continue;

                    if (!byItemId.TryGetValue(itemId, out var sku))
                    {
                        sku = new Sku { ItemId = itemId, Product = p, RetailerHost = host };
                        _db.Skus.Add(sku);
                    }

                    sku.Name = it["name"]?.ToString();
                    sku.NameComplete = it["nameComplete"]?.ToString();
                    sku.Ean = it["ean"]?.ToString();
                    sku.MeasurementUnit = it["measurementUnit"]?.ToString();
                    sku.UnitMultiplier = TryDecimal(it["unitMultiplier"]) ?? 1m;

                    if (it["sellers"] is JsonArray sellersArray)
                    {
                        var sellersInDb = sku.Sellers.ToDictionary(s => s.SellerId, s => s);
                        foreach (var sellerNode in sellersArray.OfType<JsonObject>())
                        {
                            var sellerId = sellerNode["sellerId"]?.ToString();
                            if (string.IsNullOrEmpty(sellerId)) continue;

                            if (!sellersInDb.TryGetValue(sellerId, out var seller))
                            {
                                seller = new Seller { SellerId = sellerId, Sku = sku };
                                _db.Sellers.Add(seller);
                            }
                            seller.SellerName = sellerNode["sellerName"]?.ToString();
                            seller.SellerDefault = (bool?)sellerNode["sellerDefault"] ?? false;
                        }
                    }
                }
            }

            await _db.SaveChangesAsync(ct);
            return (1, isNew ? 1 : 0);
        }

    }
}