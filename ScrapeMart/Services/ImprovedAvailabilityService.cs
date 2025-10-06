// Ruta: ScrapeMart/Services/ImprovedAvailabilityService.cs

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities.dtos;
using ScrapeMart.Storage;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// 🚀 SERVICIO DEFINITIVO: Verifica disponibilidad de forma completa y robusta.
/// ✅ Usa Proxy de Bright Data.
/// ✅ Usa VtexCookieManager para sesiones por host.
/// ✅ Usa el payload de simulación que SÍ funciona (addressType: "search").
/// ✅ Extrae el PickupPointId de la respuesta de la simulación.
/// ✅ Actualiza la tabla dbo.Stores con los nuevos PickupPointId encontrados.
/// ✅ Parsea los precios correctamente (Price vs ListPrice).
/// </summary>
public sealed class ImprovedAvailabilityService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ImprovedAvailabilityService> _log;
    private readonly string _sqlConn;
    private readonly IVtexCookieManager _cookieManager;
    private readonly IConfiguration _config; // Necesario para leer la config del proxy
    private readonly SemaphoreSlim _globalThrottle = new(10, 10);
    private readonly Dictionary<string, SemaphoreSlim> _hostThrottles = new();
    private readonly Dictionary<string, DateTime> _lastRequestByHost = new();
    private readonly TimeSpan _minDelayBetweenRequests = TimeSpan.FromMilliseconds(250);

    // --- CONSTRUCTOR CORREGIDO ---
    public ImprovedAvailabilityService(
        IServiceProvider serviceProvider,
        ILogger<ImprovedAvailabilityService> log,
        IConfiguration config,
        IVtexCookieManager cookieManager)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _config = config; // Inyectado
        _sqlConn = config.GetConnectionString("Default")!;
        _cookieManager = cookieManager; // Inyectado
    }

    public async Task<ComprehensiveResult> RunComprehensiveCheckAsync(
        string? specificHost = null,
        CancellationToken ct = default)
    {
        var result = new ComprehensiveResult { StartedAt = DateTime.UtcNow };
        _log.LogInformation("🚀 === INICIANDO VERIFICACIÓN COMPREHENSIVA (VERSIÓN DEFINITIVA) ===");

        // ... (el resto del método se mantiene igual, ya que la lógica principal es correcta)
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();

            var retailers = await GetEnabledRetailersAsync(db, specificHost, ct);
            result.TotalRetailers = retailers.Count;

            _log.LogInformation("🏢 Cadenas a procesar: {Count}", retailers.Count);

            var trackedProducts = await GetTrackedProductsAsync(db, ct);
            result.TotalProductsToTrack = trackedProducts.Count;

            _log.LogInformation("📋 Productos con Track=true: {Count}", trackedProducts.Count);

            if (trackedProducts.Count == 0)
            {
                _log.LogWarning("⚠️ No hay productos con Track=true");
                result.ErrorMessage = "No hay productos para trackear";
                return result;
            }

            foreach (var retailer in retailers)
            {
                if (ct.IsCancellationRequested) break;

                _log.LogInformation("🏢 === PROCESANDO {RetailerName} ({Host}) ===",
                    retailer.DisplayName, retailer.VtexHost);

                try
                {
                    var retailerResult = await ProcessRetailerAsync(retailer, trackedProducts, ct);
                    result.RetailerResults[retailer.VtexHost] = retailerResult;

                    result.TotalStoresProcessed += retailerResult.StoresProcessed;
                    result.TotalProductChecks += retailerResult.ProductChecks;
                    result.TotalAvailableProducts += retailerResult.AvailableProducts;

                    _log.LogInformation("✅ {RetailerName} completado: {Stores} sucursales, {Checks} verificaciones, {Available} disponibles",
                        retailer.DisplayName, retailerResult.StoresProcessed, retailerResult.ProductChecks, retailerResult.AvailableProducts);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "❌ Error procesando {RetailerName}", retailer.DisplayName);
                    result.RetailerResults[retailer.VtexHost] = new RetailerResult
                    {
                        RetailerHost = retailer.VtexHost,
                        ErrorMessage = ex.Message
                    };
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;

            LogFinalReport(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error en verificación comprehensiva");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<RetailerResult> ProcessRetailerAsync(
        RetailerInfo retailer,
        List<ProductToTrack> trackedProducts,
        CancellationToken ct)
    {
        // ... (este método también se mantiene, ya que su lógica es correcta)
        var result = new RetailerResult { RetailerHost = retailer.VtexHost };
        var salesChannel = retailer.SalesChannels.First();

        if (!_hostThrottles.ContainsKey(retailer.VtexHost))
        {
            _hostThrottles[retailer.VtexHost] = new SemaphoreSlim(4, 4);
        }

        await SetupCookiesAsync(retailer.VtexHost, salesChannel);

        using var httpClient = CreateHttpClientWithProxyAndCookies(retailer.VtexHost);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        var availableProducts = await GetAvailableProductsForRetailerAsync(
            db, retailer.VtexHost, trackedProducts, ct);

        if (availableProducts.Count == 0)
        {
            _log.LogWarning("⚠️ {RetailerName}: No se encontraron productos en catálogo",
                retailer.DisplayName);
            return result;
        }

        _log.LogInformation("📦 {RetailerName}: {Found}/{Total} productos encontrados",
            retailer.DisplayName, availableProducts.Count, trackedProducts.Count);

        var stores = await GetStoresForRetailerAsync(db, retailer.RetailerId, ct);

        if (stores.Count == 0)
        {
            _log.LogWarning("⚠️ {RetailerName}: No se encontraron sucursales",
                retailer.DisplayName);
            return result;
        }

        result.StoresProcessed = stores.Count;
        _log.LogInformation("📍 {RetailerName}: {StoreCount} sucursales para verificar",
            retailer.DisplayName, stores.Count);

        var tasks = new List<Task>();
        var resultsLock = new object();

        foreach (var product in availableProducts)
        {
            foreach (var store in stores)
            {
                if (ct.IsCancellationRequested) break;

                var task = ProcessProductStoreWithThrottlingAsync(
                    httpClient, retailer, product, store, salesChannel,
                    result, resultsLock, ct);

                tasks.Add(task);

                if (tasks.Count >= 50)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }

            if (ct.IsCancellationRequested) break;
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        return result;
    }

    // --- HttpClient con Proxy y Cookies (Corregido) ---
    private HttpClient CreateHttpClientWithProxyAndCookies(string host)
    {
        var cookieContainer = _cookieManager.GetCookieContainer(host);
        var proxyConfig = _config.GetSection("Proxy");

        var handler = new HttpClientHandler()
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var proxyUrl = proxyConfig["Url"];
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            var proxy = new WebProxy(new Uri(proxyUrl));
            var username = proxyConfig["Username"];
            if (!string.IsNullOrEmpty(username))
            {
                proxy.Credentials = new NetworkCredential(username, proxyConfig["Password"]);
            }
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 1.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");
        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }

    // --- TestAvailabilityAsync con Payload y Lógica de Simulación (Corregido) ---
    public async Task<AvailabilityTestResult> TestAvailabilityAsync(
        HttpClient httpClient,
        string host,
        int salesChannel,
        AvailableProduct product,
        ScrapeMart.Entities.dtos.StoreInfo store,
        CancellationToken ct)
    {
        var result = new AvailabilityTestResult
        {
            ProductEan = product.EAN,
            SkuId = product.SkuId,
            SellerId = product.SellerId
        };

        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";

        var addressPayload = new
        {
            country = "ARG",
            addressType = "search",
            addressId = "simulation",
            geoCoordinates = new[] { store.Longitude, store.Latitude }
        };

        var payload = new
        {
            country = "ARG",
            items = new[] { new { id = product.SkuId, quantity = 1, seller = product.SellerId } },
            shippingData = new
            {
                address = addressPayload,
                clearAddressIfPostalCodeNotFound = false,
                selectedAddresses = new[] { addressPayload }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        request.Headers.Add("Referer", host + "/");
        request.Headers.Add("x-requested-with", "XMLHttpRequest");

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        result.RawResponse = responseBody;
        result.StatusCode = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            result = ParseSimulationResponse(result, responseBody);
        }
        else
        {
            if (responseBody.Contains("CHK003")) result.ErrorMessage = "CHK003 - Bloqueado";
            else if (responseBody.Contains("CHK002")) result.ErrorMessage = "CHK002 - Request inválido";
            else result.ErrorMessage = $"HTTP {response.StatusCode}";
        }

        return result;
    }

    // --- ParseSimulationResponse con Extracción de PickupPointId y Precios (Corregido) ---
    private static AvailabilityTestResult ParseSimulationResponse(AvailabilityTestResult result, string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                var item = items[0];

                if (item.TryGetProperty("availability", out var avail))
                {
                    var availStr = avail.GetString();
                    result.IsAvailable = availStr == "available";
                    if (availStr == "withoutStock") result.ErrorMessage = "Sin stock";
                    else if (availStr == "cannotBeDelivered") result.ErrorMessage = "No se puede entregar";
                }

                if (item.TryGetProperty("sellingPrice", out var sp) && sp.ValueKind == JsonValueKind.Number)
                    result.Price = sp.GetDecimal() / 100m;

                if (item.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number)
                    result.ListPrice = p.GetDecimal() / 100m;

                if (item.TryGetProperty("quantity", out var qty) && qty.ValueKind == JsonValueKind.Number)
                    result.AvailableQuantity = qty.GetInt32();
            }

            if (doc.RootElement.TryGetProperty("logisticsInfo", out var logistics) &&
                logistics.ValueKind == JsonValueKind.Array &&
                logistics.GetArrayLength() > 0)
            {
                var logistic = logistics[0];
                if (logistic.TryGetProperty("slas", out var slas) && slas.ValueKind == JsonValueKind.Array)
                {
                    var pickupSla = slas.EnumerateArray()
                        .FirstOrDefault(sla => sla.TryGetProperty("deliveryChannel", out var dc) && dc.GetString() == "pickup-in-point");

                    if (pickupSla.ValueKind != JsonValueKind.Undefined && pickupSla.TryGetProperty("pickupPointId", out var ppId))
                    {
                        result.FoundPickupPointId = ppId.GetString();
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("storePreferencesData", out var prefs) &&
                prefs.TryGetProperty("currencyCode", out var currency))
            {
                result.Currency = currency.GetString() ?? "ARS";
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Parse error: {ex.Message}";
            result.IsAvailable = false;
        }

        return result;
    }

    // --- SaveAvailabilityResultAsync con Actualización de dbo.Stores (Corregido) ---
    private async Task SaveAvailabilityResultAsync(
      string host,
      ScrapeMart.Entities.dtos.StoreInfo store,
      AvailableProduct product,
      AvailabilityTestResult availability,
      int salesChannel,
      CancellationToken ct)
    {
        const string availabilitySql = @"
        MERGE dbo.VtexStoreAvailability AS T
        USING (SELECT @host AS RetailerHost, @pp AS PickupPointId, @sku AS SkuId, @seller AS SellerId, @sc AS SalesChannel, @capturedAt AS CapturedAtUtc) AS S
        ON (T.RetailerHost = S.RetailerHost AND T.PickupPointId = S.PickupPointId AND T.SkuId = S.SkuId AND T.SellerId = S.SellerId AND T.SalesChannel = S.SalesChannel)
        WHEN MATCHED AND T.CapturedAtUtc < S.CapturedAtUtc THEN
          UPDATE SET 
            IsAvailable=@avail, 
            MaxFeasibleQty=@maxQty, 
            Price=@price, 
            ListPrice=@listPrice, 
            Currency=@curr, 
            CountryCode=@country, 
            PostalCode=@postal, 
            CapturedAtUtc=@capturedAt, 
            RawJson=@raw, 
            ErrorMessage=@error
        WHEN NOT MATCHED THEN
          INSERT (RetailerHost, PickupPointId, SkuId, SellerId, SalesChannel, CountryCode, PostalCode, IsAvailable, MaxFeasibleQty, Price, ListPrice, Currency, CapturedAtUtc, RawJson, ErrorMessage)
          VALUES (@host, @pp, @sku, @seller, @sc, @country, @postal, @avail, @maxQty, @price, @listPrice, @curr, @capturedAt, @raw, @error);";

        try
        {
            await using var connection = new SqlConnection(_sqlConn);
            await connection.OpenAsync(ct);

            var pickupPointToSave = availability.FoundPickupPointId
                                   ?? store.VtexPickupPointId
                                   ?? store.StoreId.ToString();

            var capturedAt = DateTime.UtcNow;

            await using (var command = new SqlCommand(availabilitySql, connection))
            {
                command.Parameters.AddWithValue("@pp", pickupPointToSave);
                command.Parameters.AddWithValue("@host", host);
                command.Parameters.AddWithValue("@sku", product.SkuId);
                command.Parameters.AddWithValue("@seller", product.SellerId);
                command.Parameters.AddWithValue("@sc", salesChannel);
                command.Parameters.AddWithValue("@country", "AR");
                command.Parameters.AddWithValue("@postal", store.PostalCode);
                command.Parameters.AddWithValue("@avail", availability.IsAvailable);
                command.Parameters.AddWithValue("@maxQty", availability.AvailableQuantity);
                command.Parameters.AddWithValue("@price", (object?)availability.Price ?? DBNull.Value);
                command.Parameters.AddWithValue("@listPrice", (object?)availability.ListPrice ?? DBNull.Value);
                command.Parameters.AddWithValue("@curr", availability.Currency ?? "ARS");
                command.Parameters.AddWithValue("@raw", (object?)availability.RawResponse?.Substring(0, Math.Min(availability.RawResponse.Length, 4000)) ?? DBNull.Value);
                command.Parameters.AddWithValue("@error", (object?)availability.ErrorMessage ?? DBNull.Value);
                command.Parameters.AddWithValue("@capturedAt", capturedAt);
                await command.ExecuteNonQueryAsync(ct);
            }

            if (!string.IsNullOrEmpty(availability.FoundPickupPointId) && store.VtexPickupPointId != availability.FoundPickupPointId)
            {
                _log.LogInformation("📢 ¡Nuevo PickupPointId encontrado! Actualizando Store {StoreId}: {PickupId}", store.StoreId, availability.FoundPickupPointId);
                const string updateStoreSql = "UPDATE dbo.Stores SET VtexPickupPointId = @pickupId, LastVtexSync = GETUTCDATE() WHERE StoreId = @storeId";
                await using (var updateCmd = new SqlCommand(updateStoreSql, connection))
                {
                    updateCmd.Parameters.AddWithValue("@pickupId", availability.FoundPickupPointId);
                    updateCmd.Parameters.AddWithValue("@storeId", store.StoreId);
                    await updateCmd.ExecuteNonQueryAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error guardando resultado para {Product} en {Store}", product.ProductName, store.StoreName);
        }
    }
    // --- El resto de los métodos (helpers para throttling, carga de datos, etc.) no necesitan cambios y se mantienen ---

    private async Task ProcessProductStoreWithThrottlingAsync(
        HttpClient httpClient,
        RetailerInfo retailer,
        AvailableProduct product,
        ScrapeMart.Entities.dtos.StoreInfo store,
        int salesChannel,
        RetailerResult result,
        object resultsLock,
        CancellationToken ct)
    {
        await _globalThrottle.WaitAsync(ct);

        try
        {
            await _hostThrottles[retailer.VtexHost].WaitAsync(ct);

            try
            {
                await EnforceMinimumDelayAsync(retailer.VtexHost);

                var availability = await TestAvailabilityWithRetryAsync(
                    httpClient, retailer.VtexHost, salesChannel, product, store, ct);

                await SaveAvailabilityResultAsync(
                    retailer.VtexHost, store, product, availability, salesChannel, ct);

                lock (resultsLock)
                {
                    result.ProductChecks++;
                    if (availability.IsAvailable) result.AvailableProducts++;

                    if (result.ProductChecks % 100 == 0)
                    {
                        _log.LogInformation("📊 {RetailerName}: {Completed} verificaciones completadas",
                            retailer.DisplayName, result.ProductChecks);
                    }
                }

                if (availability.IsAvailable)
                {
                    _log.LogDebug("✅ {Product} disponible en {Store}",
                        product.ProductName, store.StoreName);
                }
            }
            finally
            {
                _hostThrottles[retailer.VtexHost].Release();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error verificando {Product} en {Store}",
                product.ProductName, store.StoreName);

            lock (resultsLock)
            {
                result.ProductChecks++;
            }
        }
        finally
        {
            _globalThrottle.Release();
        }
    }

    private async Task EnforceMinimumDelayAsync(string host)
    {
        lock (_lastRequestByHost)
        {
            if (_lastRequestByHost.TryGetValue(host, out var lastRequest))
            {
                var timeSinceLastRequest = DateTime.UtcNow - lastRequest;
                if (timeSinceLastRequest < _minDelayBetweenRequests)
                {
                    var delayNeeded = _minDelayBetweenRequests - timeSinceLastRequest;
                    Thread.Sleep(delayNeeded);
                }
            }

            _lastRequestByHost[host] = DateTime.UtcNow;
        }
    }

    private async Task<AvailabilityTestResult> TestAvailabilityWithRetryAsync(
        HttpClient httpClient,
        string host,
        int salesChannel,
        AvailableProduct product,
        ScrapeMart.Entities.dtos.StoreInfo store,
        CancellationToken ct)
    {
        var maxRetries = 3;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await TestAvailabilityAsync(httpClient, host, salesChannel, product, store, ct);
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _log.LogWarning("⚠️ Intento {Attempt}/{Max} falló para {Product} en {Store}. Reintentando en {Delay}s...",
                        attempt, maxRetries, product.ProductName, store.StoreName, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }
            }
        }

        return new AvailabilityTestResult
        {
            ProductEan = product.EAN,
            SkuId = product.SkuId,
            SellerId = product.SellerId,
            ErrorMessage = $"Falló después de {maxRetries} intentos: {lastException?.Message}"
        };
    }

    private async Task SetupCookiesAsync(string host, int salesChannel)
    {
        _log.LogDebug("🍪 Configurando cookies para {Host} (SC: {SalesChannel})", host, salesChannel);
        using var tempClient = CreateHttpClientWithProxyAndCookies(host);
        await _cookieManager.WarmupCookiesAsync(tempClient, host);
        _cookieManager.UpdateSegmentCookie(host, salesChannel);
    }

    private async Task<List<RetailerInfo>> GetEnabledRetailersAsync(AppDb db, string? specificHost, CancellationToken ct)
    {
        var rawData = await (
            from retailer in db.Retailers.AsNoTracking()
            join config in db.VtexRetailersConfigs.AsNoTracking() on retailer.RetailerId equals config.RetailerId
            where config.Enabled && retailer.IsActive
            where specificHost == null || config.RetailerHost == specificHost
            select new
            {
                retailer.RetailerId,
                retailer.DisplayName,
                VtexHost = retailer.VtexHost ?? retailer.PublicHost!,
                SalesChannelsString = config.SalesChannels,
                StoreCount = retailer.Stores.Count(s => s.IsActive)
            }
        ).ToListAsync(ct);

        return rawData.Select(r => new RetailerInfo
        {
            RetailerId = r.RetailerId,
            DisplayName = r.DisplayName,
            VtexHost = r.VtexHost,
            SalesChannels = r.SalesChannelsString
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var result) ? result : 1)
                .ToArray(),
            StoreCount = r.StoreCount
        }).ToList();
    }

    private async Task<List<ProductToTrack>> GetTrackedProductsAsync(AppDb db, CancellationToken ct)
    {
        return await db.ProductsToTrack
            .Where(p => p.Track.HasValue && p.Track.Value == true)
            .AsNoTracking()
            .Select(p => new ProductToTrack
            {
                EAN = p.EAN,
                Owner = p.Owner,
                ProductName = p.ProductName
            })
            .ToListAsync(ct);
    }

    private async Task<List<AvailableProduct>> GetAvailableProductsForRetailerAsync(
        AppDb db, string host, List<ProductToTrack> trackedProducts, CancellationToken ct)
    {
        var targetEans = trackedProducts.Select(p => p.EAN).ToList();
        var productLookup = trackedProducts.ToDictionary(p => p.EAN, p => p);

        var sellersData = await (
            from seller in db.Sellers.AsNoTracking()
            where MyDbFunctions.NormalizeHost(seller.Sku.RetailerHost) == MyDbFunctions.NormalizeHost(host)
            where seller.Sku.Ean != null && targetEans.Contains(seller.Sku.Ean)
            select new
            {
                EAN = seller.Sku.Ean!,
                SkuId = seller.Sku.ItemId,
                seller.SellerId
            }
        ).Distinct().ToListAsync(ct);

        return sellersData
            .Where(s => productLookup.ContainsKey(s.EAN))
            .Select(s => new AvailableProduct
            {
                EAN = s.EAN,
                SkuId = s.SkuId,
                SellerId = s.SellerId,
                ProductName = productLookup[s.EAN].ProductName ?? "Sin nombre",
                Owner = productLookup[s.EAN].Owner
            })
            .ToList();
    }

    private async Task<List<ScrapeMart.Entities.dtos.StoreInfo>> GetStoresForRetailerAsync(AppDb db, string retailerId, CancellationToken ct)
    {
        return await db.Stores
            .AsNoTracking()
            .Where(s => s.RetailerId == retailerId && s.IsActive)
            .Where(s => !string.IsNullOrEmpty(s.PostalCode))
            .Select(s => new ScrapeMart.Entities.dtos.StoreInfo
            {
                StoreId = s.StoreId,
                StoreName = s.StoreName,
                City = s.City,
                Province = s.Province,
                PostalCode = s.PostalCode!,
                VtexPickupPointId = s.VtexPickupPointId,
                Latitude = (double)s.Latitude,
                Longitude = (double)s.Longitude
            })
            .ToListAsync(ct);
    }

    private void LogFinalReport(ComprehensiveResult result)
    {
        _log.LogInformation("🎉 === REPORTE FINAL DE DISPONIBILIDAD ===");
        _log.LogInformation("📊 ESTADÍSTICAS GENERALES:");
        _log.LogInformation("  ⏱️ Duración: {Duration:F1} minutos", result.Duration.TotalMinutes);
        _log.LogInformation("  🏢 Cadenas procesadas: {Retailers}", result.TotalRetailers);
        _log.LogInformation("  📋 Productos trackeados: {Products}", result.TotalProductsToTrack);
        _log.LogInformation("  📍 Sucursales procesadas: {Stores}", result.TotalStoresProcessed);
        _log.LogInformation("  🧪 Total verificaciones: {Total}", result.TotalProductChecks);
        _log.LogInformation("  ✅ Productos disponibles: {Available}", result.TotalAvailableProducts);
        _log.LogInformation("  📈 Tasa de disponibilidad: {Rate:P2}",
            result.TotalProductChecks > 0 ? (double)result.TotalAvailableProducts / result.TotalProductChecks : 0);

        _log.LogInformation("📋 DESGLOSE POR CADENA:");
        foreach (var (host, retailerResult) in result.RetailerResults)
        {
            var rate = retailerResult.ProductChecks > 0 ?
                (double)retailerResult.AvailableProducts / retailerResult.ProductChecks : 0;

            _log.LogInformation("  🏢 {Host}:", host);
            _log.LogInformation("    • Sucursales: {Stores}", retailerResult.StoresProcessed);
            _log.LogInformation("    • Verificaciones: {Checks}", retailerResult.ProductChecks);
            _log.LogInformation("    • Disponibles: {Available} ({Rate:P2})",
                retailerResult.AvailableProducts, rate);

            if (!string.IsNullOrEmpty(retailerResult.ErrorMessage))
            {
                _log.LogWarning("    ❌ Error: {Error}", retailerResult.ErrorMessage);
            }
        }
    }

}