// File: Services/VtexProductDiscoveryService.cs
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Text.Json;
using System.Net;

namespace ScrapeMart.Services;

/// <summary>
/// 🔍 Servicio para descubrir y mapear TODOS los SKUs y sellers de productos trackeados
/// en TODAS las cadenas VTEX habilitadas
/// </summary>
public sealed class VtexProductDiscoveryService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VtexProductDiscoveryService> _log;
    private readonly string _sqlConn;
    private readonly IVtexCookieManager _cookieManager;

    public VtexProductDiscoveryService(
        IServiceProvider serviceProvider,
        ILogger<VtexProductDiscoveryService> log,
        IConfiguration cfg,
        IVtexCookieManager cookieManager)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
        _cookieManager = cookieManager;
    }

    /// <summary>
    /// 🚀 MÉTODO PRINCIPAL: Descubre SKUs y sellers para TODOS los productos de ProductsToTrack
    /// en TODAS las cadenas VTEX habilitadas
    /// </summary>
    public async Task<DiscoveryResult> DiscoverAllProductsInAllChainsAsync(
        string? specificHost = null,
        CancellationToken ct = default)
    {
        _log.LogInformation("🔍 === INICIANDO DISCOVERY MASIVO DE PRODUCTOS ===");

        var result = new DiscoveryResult { StartedAt = DateTime.UtcNow };

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        try
        {
            // 1️⃣ OBTENER TODAS LAS CADENAS HABILITADAS
            var retailers = await db.VtexRetailersConfigs
                .Where(r => r.Enabled && (specificHost == null || r.RetailerHost == specificHost))
                .AsNoTracking()
                .ToListAsync(ct);

            _log.LogInformation("🏢 Cadenas a procesar: {Count}", retailers.Count);

            // 2️⃣ OBTENER TODOS LOS PRODUCTOS A TRACKEAR
            var productsToTrack = await db.ProductsToTrack
                .Where(p => p.Track.HasValue && p.Track.Value == true)
                .AsNoTracking()
                .Select(p => new ProductToDiscover
                {
                    EAN = p.EAN,
                    Owner = p.Owner,
                    ProductName = p.ProductName ?? "Sin nombre"
                })
                .ToListAsync(ct);

            _log.LogInformation("📋 Productos a buscar: {Count} ({AdecoCount} Adeco + {CompetitorCount} competencia)",
                productsToTrack.Count,
                productsToTrack.Count(p => p.Owner == "Adeco"),
                productsToTrack.Count(p => p.Owner != "Adeco"));

            result.TotalProductsToDiscover = productsToTrack.Count;
            result.TotalRetailers = retailers.Count;

            // 3️⃣ PROCESAR CADA CADENA
            foreach (var retailer in retailers)
            {
                if (ct.IsCancellationRequested) break;

                _log.LogInformation("🏢 === PROCESANDO {Host} ===", retailer.RetailerHost);

                var retailerResult = await ProcessRetailerDiscoveryAsync(
                    retailer,
                    productsToTrack,
                    ct);

                result.RetailerResults[retailer.RetailerHost] = retailerResult;
                result.TotalProductsFound += retailerResult.ProductsFound;
                result.TotalSkusDiscovered += retailerResult.SkusDiscovered;
                result.TotalSellersDiscovered += retailerResult.SellersDiscovered;

                _log.LogInformation("✅ {Host} completado: {Products} productos, {Skus} SKUs, {Sellers} sellers",
                    retailer.RetailerHost,
                    retailerResult.ProductsFound,
                    retailerResult.SkusDiscovered,
                    retailerResult.SellersDiscovered);

                // Pausa entre cadenas para evitar rate limiting
                await Task.Delay(2000, ct);
            }

            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;

            // 4️⃣ GENERAR REPORTE FINAL
            LogFinalReport(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error en discovery masivo");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// 🏢 Procesar discovery para una cadena específica
    /// </summary>
    private async Task<RetailerDiscoveryResult> ProcessRetailerDiscoveryAsync(
        Entities.VtexRetailersConfig retailer,
        List<ProductToDiscover> productsToDiscover,
        CancellationToken ct)
    {
        var result = new RetailerDiscoveryResult
        {
            RetailerHost = retailer.RetailerHost
        };

        var salesChannel = int.Parse(retailer.SalesChannels.Split(',').First());

        // 🍪 CONFIGURAR COOKIES PARA ESTA CADENA
        await SetupCookiesForRetailer(retailer.RetailerHost, salesChannel);

        // 🍪 CREAR CLIENTE CON COOKIES
        using var httpClient = CreateClientWithCookieManager(retailer.RetailerHost);

        // 🔍 BUSCAR CADA PRODUCTO POR EAN
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, // Controlamos concurrencia para no saturar
            CancellationToken = ct
        };

        var discoveredProducts = new List<DiscoveredProduct>();
        var lockObject = new object();

        await Parallel.ForEachAsync(productsToDiscover, parallelOptions, async (productToDiscover, token) =>
        {
            if (token.IsCancellationRequested) return;

            try
            {
                var discoveredProduct = await SearchProductByEanAsync(
                    httpClient,
                    retailer.RetailerHost,
                    salesChannel,
                    productToDiscover,
                    token);

                if (discoveredProduct != null)
                {
                    lock (lockObject)
                    {
                        discoveredProducts.Add(discoveredProduct);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error buscando {EAN} en {Host}",
                    productToDiscover.EAN, retailer.RetailerHost);
            }
        });

        // 📊 PERSISTIR RESULTADOS
        if (discoveredProducts.Count > 0)
        {
            await PersistDiscoveredProductsAsync(retailer.RetailerHost, discoveredProducts, ct);

            result.ProductsFound = discoveredProducts.Count;
            result.SkusDiscovered = discoveredProducts.Sum(p => p.Skus.Count);
            result.SellersDiscovered = discoveredProducts.Sum(p => p.Skus.Sum(s => s.Sellers.Count));

            // Detalles por marca
            result.AdecoProductsFound = discoveredProducts.Count(p => p.Owner == "Adeco");
            result.CompetitorProductsFound = discoveredProducts.Count(p => p.Owner != "Adeco");
        }

        return result;
    }

    /// <summary>
    /// 🔍 Buscar un producto específico por EAN en una cadena
    /// </summary>
    private async Task<DiscoveredProduct?> SearchProductByEanAsync(
        HttpClient httpClient,
        string host,
        int salesChannel,
        ProductToDiscover productToDiscover,
        CancellationToken ct)
    {
        try
        {
            // URL de búsqueda por EAN
            var searchUrl = $"{host.TrimEnd('/')}/api/catalog_system/pub/products/search?ft={productToDiscover.EAN}&_from=0&_to=0&sc={salesChannel}";

            using var response = await httpClient.GetAsync(searchUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Búsqueda fallida para EAN {EAN} en {Host}: HTTP {Status}",
                    productToDiscover.EAN, host, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);

            if (string.IsNullOrEmpty(json) || json == "[]")
            {
                _log.LogDebug("No se encontró producto para EAN {EAN} en {Host}",
                    productToDiscover.EAN, host);
                return null;
            }

            // Parsear respuesta
            return ParseProductSearchResponse(productToDiscover, json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error buscando EAN {EAN} en {Host}",
                productToDiscover.EAN, host);
            return null;
        }
    }

    /// <summary>
    /// 📋 Parsear respuesta de búsqueda de producto
    /// </summary>
    private DiscoveredProduct? ParseProductSearchResponse(ProductToDiscover productToDiscover, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array ||
                doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var productElement = doc.RootElement[0]; // Tomamos el primer resultado

            var discoveredProduct = new DiscoveredProduct
            {
                EAN = productToDiscover.EAN,
                Owner = productToDiscover.Owner,
                TrackedProductName = productToDiscover.ProductName
            };

            // Extraer información del producto
            if (productElement.TryGetProperty("productId", out var productId))
                discoveredProduct.ProductId = productId.GetString();

            if (productElement.TryGetProperty("productName", out var productName))
                discoveredProduct.ProductName = productName.GetString();

            if (productElement.TryGetProperty("brand", out var brand))
                discoveredProduct.Brand = brand.GetString();

            if (productElement.TryGetProperty("linkText", out var linkText))
                discoveredProduct.LinkText = linkText.GetString();

            // Extraer SKUs y sellers
            if (productElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var sku = ParseSkuFromItem(item);
                    if (sku != null)
                    {
                        discoveredProduct.Skus.Add(sku);
                    }
                }
            }

            // Guardar JSON completo para auditoría
            discoveredProduct.RawJson = json;

            _log.LogInformation("✅ Encontrado: {ProductName} ({EAN}) - {SkuCount} SKUs, {SellerCount} sellers",
                discoveredProduct.ProductName,
                discoveredProduct.EAN,
                discoveredProduct.Skus.Count,
                discoveredProduct.Skus.Sum(s => s.Sellers.Count));

            return discoveredProduct;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error parseando respuesta para EAN {EAN}", productToDiscover.EAN);
            return null;
        }
    }

    /// <summary>
    /// 📦 Parsear SKU desde item
    /// </summary>
    private DiscoveredSku? ParseSkuFromItem(JsonElement item)
    {
        try
        {
            var sku = new DiscoveredSku();

            if (item.TryGetProperty("itemId", out var itemId))
                sku.SkuId = itemId.GetString();

            if (item.TryGetProperty("name", out var name))
                sku.Name = name.GetString();

            if (item.TryGetProperty("nameComplete", out var nameComplete))
                sku.NameComplete = nameComplete.GetString();

            if (item.TryGetProperty("ean", out var ean))
                sku.Ean = ean.GetString();

            // Verificar que el EAN del SKU coincida o sea vacío
            // (algunos productos tienen múltiples SKUs con diferentes EANs)

            // Extraer sellers
            if (item.TryGetProperty("sellers", out var sellers) &&
                sellers.ValueKind == JsonValueKind.Array)
            {
                foreach (var seller in sellers.EnumerateArray())
                {
                    var sellerInfo = ParseSellerFromElement(seller);
                    if (sellerInfo != null)
                    {
                        sku.Sellers.Add(sellerInfo);
                    }
                }
            }

            return !string.IsNullOrEmpty(sku.SkuId) ? sku : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 🏪 Parsear seller desde elemento
    /// </summary>
    private DiscoveredSeller? ParseSellerFromElement(JsonElement seller)
    {
        try
        {
            var sellerInfo = new DiscoveredSeller();

            if (seller.TryGetProperty("sellerId", out var sellerId))
                sellerInfo.SellerId = sellerId.GetString();

            if (seller.TryGetProperty("sellerName", out var sellerName))
                sellerInfo.SellerName = sellerName.GetString();

            if (seller.TryGetProperty("sellerDefault", out var sellerDefault))
                sellerInfo.IsDefault = sellerDefault.GetBoolean();

            // Extraer información de precio/disponibilidad si está presente
            if (seller.TryGetProperty("commertialOffer", out var offer))
            {
                if (offer.TryGetProperty("IsAvailable", out var isAvailable))
                    sellerInfo.IsAvailable = isAvailable.GetBoolean();

                if (offer.TryGetProperty("Price", out var price))
                    sellerInfo.Price = price.GetDecimal();

                if (offer.TryGetProperty("ListPrice", out var listPrice))
                    sellerInfo.ListPrice = listPrice.GetDecimal();

                if (offer.TryGetProperty("AvailableQuantity", out var quantity))
                    sellerInfo.AvailableQuantity = quantity.GetInt32();
            }

            return !string.IsNullOrEmpty(sellerInfo.SellerId) ? sellerInfo : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 💾 Persistir productos descubiertos en la base de datos
    /// </summary>
    private async Task PersistDiscoveredProductsAsync(
        string host,
        List<DiscoveredProduct> discoveredProducts,
        CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(_sqlConn);
            await connection.OpenAsync(ct);

            using var transaction = connection.BeginTransaction();

            foreach (var product in discoveredProducts)
            {
                // Guardar producto principal
                await UpsertProductAsync(connection, transaction, host, product, ct);

                // Guardar SKUs y sellers
                foreach (var sku in product.Skus)
                {
                    await UpsertSkuAsync(connection, transaction, host, product.ProductId!, sku, ct);

                    foreach (var seller in sku.Sellers)
                    {
                        await UpsertSellerAsync(connection, transaction, host, sku.SkuId!, seller, ct);
                    }
                }
            }

            transaction.Commit();

            _log.LogInformation("💾 Persistidos {Count} productos con sus SKUs y sellers para {Host}",
                discoveredProducts.Count, host);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error persistiendo productos descobertos");
            throw;
        }
    }

    /// <summary>
    /// 💾 Upsert de producto
    /// </summary>
    private async Task UpsertProductAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string host,
        DiscoveredProduct product,
        CancellationToken ct)
    {
        const string sql = @"
            MERGE dbo.VtexProducts AS T
            USING (SELECT @host AS RetailerHost, @productId AS ProductId) AS S
            ON (T.RetailerHost = S.RetailerHost AND T.ProductId = S.ProductId)
            WHEN MATCHED THEN 
                UPDATE SET 
                    ProductName = @productName,
                    Brand = @brand,
                    LinkText = @linkText,
                    LastSeenUtc = SYSUTCDATETIME(),
                    RawJson = @rawJson
            WHEN NOT MATCHED THEN 
                INSERT (RetailerHost, ProductId, ProductName, Brand, LinkText, FirstSeenUtc, LastSeenUtc, RawJson)
                VALUES (@host, @productId, @productName, @brand, @linkText, SYSUTCDATETIME(), SYSUTCDATETIME(), @rawJson);";

        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@productId", (object?)product.ProductId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@productName", (object?)product.ProductName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@brand", (object?)product.Brand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@linkText", (object?)product.LinkText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rawJson",
            (object?)product.RawJson?.Substring(0, Math.Min(product.RawJson.Length, 8000)) ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 💾 Upsert de SKU
    /// </summary>
    private async Task UpsertSkuAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string host,
        string productId,
        DiscoveredSku sku,
        CancellationToken ct)
    {
        const string sql = @"
            MERGE dbo.VtexSkus AS T
            USING (SELECT @host AS RetailerHost, @skuId AS SkuId) AS S
            ON (T.RetailerHost = S.RetailerHost AND T.SkuId = S.SkuId)
            WHEN MATCHED THEN 
                UPDATE SET 
                    ProductId = @productId,
                    SkuName = @name,
                    Ean = @ean,
                    LastSeenUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT (RetailerHost, SkuId, ProductId, SkuName, Ean, FirstSeenUtc, LastSeenUtc)
                VALUES (@host, @skuId, @productId, @name, @ean, SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@skuId", (object?)sku.SkuId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@productId", (object?)productId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", (object?)sku.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ean", (object?)sku.Ean ?? DBNull.Value);

        int v = await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 💾 Upsert de seller
    /// </summary>
    private async Task UpsertSellerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string host,
        string skuId,
        DiscoveredSeller seller,
        CancellationToken ct)
    {
        const string sql = @"
            MERGE dbo.VtexSkuSellers AS T
            USING (SELECT @host AS RetailerHost, @skuId AS SkuId, @sellerId AS SellerId) AS S
            ON (T.RetailerHost = S.RetailerHost AND T.SkuId = S.SkuId AND T.SellerId = S.SellerId)
            WHEN MATCHED THEN 
                UPDATE SET 
                    SellerName = @sellerName,
                    SellerDefault = @isDefault,
                    LastSeenUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT (RetailerHost, SkuId, SellerId, SellerName, SellerDefault, FirstSeenUtc, LastSeenUtc)
                VALUES (@host, @skuId, @sellerId, @sellerName, @isDefault, SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@skuId", (object?)skuId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sellerId", (object?)seller.SellerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sellerName", (object?)seller.SellerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isDefault", seller.IsDefault);

        int rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

        // Si hay información de disponibilidad, también la guardamos
        if (seller.IsAvailable.HasValue && seller.Price.HasValue)
        {
            await InsertOfferAsync(connection, transaction, host, skuId, seller, ct);
        }
    }

    /// <summary>
    /// 💾 Insertar oferta comercial
    /// </summary>
    private async Task InsertOfferAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string host,
        string skuId,
        DiscoveredSeller seller,
        CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO dbo.VtexOffers 
            (RetailerHost, SkuId, SellerId, Price, ListPrice, AvailableQuantity, IsAvailable, CapturedAtUtc)
            VALUES 
            (@host, @skuId, @sellerId, @price, @listPrice, @quantity, @available, SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@skuId", skuId);
        cmd.Parameters.AddWithValue("@sellerId", seller.SellerId);
        cmd.Parameters.AddWithValue("@price", seller.Price ?? 0);
        cmd.Parameters.AddWithValue("@listPrice", seller.ListPrice ?? 0);
        cmd.Parameters.AddWithValue("@quantity", seller.AvailableQuantity ?? 0);
        cmd.Parameters.AddWithValue("@available", seller.IsAvailable ?? false);

        int rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 🍪 Configurar cookies específicas por cadena
    /// </summary>
    private async Task SetupCookiesForRetailer(string host, int salesChannel)
    {
        _log.LogDebug("🍪 Configurando cookies para {Host} (SC: {SalesChannel})", host, salesChannel);

        // Usar el warmup del cookie manager
        using var tempClient = CreateClientWithCookieManager(host);
        await _cookieManager.WarmupCookiesAsync(tempClient, host);

        // Actualizar segment cookie con sales channel correcto
        _cookieManager.UpdateSegmentCookie(host, salesChannel);
    }

    /// <summary>
    /// 🍪 Crear HttpClient con cookies del manager
    /// </summary>
    private HttpClient CreateClientWithCookieManager(string host)
    {
        var cookieContainer = _cookieManager.GetCookieContainer(host);
        var handler = new HttpClientHandler()
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");
        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }

    /// <summary>
    /// 📊 Log del reporte final
    /// </summary>
    private void LogFinalReport(DiscoveryResult result)
    {
        _log.LogInformation("🎉 === REPORTE FINAL DE DISCOVERY ===");
        _log.LogInformation("📊 ESTADÍSTICAS GENERALES:");
        _log.LogInformation("  ⏱️ Duración: {Duration:F1} minutos",
            result.Duration.TotalMinutes);
        _log.LogInformation("  🏢 Cadenas procesadas: {Retailers}",
            result.TotalRetailers);
        _log.LogInformation("  📋 Productos buscados: {Total}",
            result.TotalProductsToDiscover);
        _log.LogInformation("  ✅ Productos encontrados: {Found} ({Rate:P1})",
            result.TotalProductsFound,
            result.TotalProductsToDiscover > 0 ?
                (double)result.TotalProductsFound / result.TotalProductsToDiscover : 0);
        _log.LogInformation("  📦 SKUs descubiertos: {Skus}",
            result.TotalSkusDiscovered);
        _log.LogInformation("  🏪 Sellers descubiertos: {Sellers}",
            result.TotalSellersDiscovered);

        _log.LogInformation("📋 DESGLOSE POR CADENA:");
        foreach (var (host, retailerResult) in result.RetailerResults)
        {
            _log.LogInformation("  🏢 {Host}:", host);
            _log.LogInformation("    • Productos: {Products} ({Adeco} Adeco, {Competitor} competencia)",
                retailerResult.ProductsFound,
                retailerResult.AdecoProductsFound,
                retailerResult.CompetitorProductsFound);
            _log.LogInformation("    • SKUs: {Skus}",
                retailerResult.SkusDiscovered);
            _log.LogInformation("    • Sellers: {Sellers}",
                retailerResult.SellersDiscovered);

            if (!string.IsNullOrEmpty(retailerResult.ErrorMessage))
            {
                _log.LogWarning("    ❌ Error: {Error}", retailerResult.ErrorMessage);
            }
        }
    }

    #region DTOs

    private sealed class ProductToDiscover
    {
        public string EAN { get; set; } = default!;
        public string Owner { get; set; } = default!;
        public string ProductName { get; set; } = default!;
    }

    private sealed class DiscoveredProduct
    {
        public string EAN { get; set; } = default!;
        public string Owner { get; set; } = default!;
        public string TrackedProductName { get; set; } = default!;
        public string? ProductId { get; set; }
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public string? LinkText { get; set; }
        public List<DiscoveredSku> Skus { get; set; } = new();
        public string? RawJson { get; set; }
    }

    private sealed class DiscoveredSku
    {
        public string? SkuId { get; set; }
        public string? Name { get; set; }
        public string? NameComplete { get; set; }
        public string? Ean { get; set; }
        public List<DiscoveredSeller> Sellers { get; set; } = new();
    }

    private sealed class DiscoveredSeller
    {
        public string? SellerId { get; set; }
        public string? SellerName { get; set; }
        public bool IsDefault { get; set; }
        public bool? IsAvailable { get; set; }
        public decimal? Price { get; set; }
        public decimal? ListPrice { get; set; }
        public int? AvailableQuantity { get; set; }
    }

    public sealed class DiscoveryResult
    {
        public bool Success { get; set; }
        public int TotalRetailers { get; set; }
        public int TotalProductsToDiscover { get; set; }
        public int TotalProductsFound { get; set; }
        public int TotalSkusDiscovered { get; set; }
        public int TotalSellersDiscovered { get; set; }
        public Dictionary<string, RetailerDiscoveryResult> RetailerResults { get; set; } = new();
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan Duration => CompletedAt.HasValue ?
            CompletedAt.Value - StartedAt : TimeSpan.Zero;
        public string? ErrorMessage { get; set; }
    }

    public sealed class RetailerDiscoveryResult
    {
        public string RetailerHost { get; set; } = default!;
        public int ProductsFound { get; set; }
        public int SkusDiscovered { get; set; }
        public int SellersDiscovered { get; set; }
        public int AdecoProductsFound { get; set; }
        public int CompetitorProductsFound { get; set; }
        public string? ErrorMessage { get; set; }
    }

    #endregion
}