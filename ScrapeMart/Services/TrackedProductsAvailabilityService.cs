// File: Services/ImprovedAvailabilityService.cs
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// 🚀 Servicio mejorado para verificación masiva de disponibilidad
/// - Sin cookies hardcodeadas (usa VtexCookieManager)
/// - Filtra por Track = true
/// - Control de velocidad (throttling)
/// - Usa proxy configurado
/// - Reintentos inteligentes
/// - Mejor manejo de errores
/// </summary>
public sealed class ImprovedAvailabilityService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ImprovedAvailabilityService> _log;
    private readonly string _sqlConn;
    private readonly IVtexCookieManager _cookieManager;
    private readonly IConfiguration _config;

    // Configuración de throttling
    private readonly SemaphoreSlim _globalThrottle = new(10, 10); // Max 10 requests simultáneos globalmente
    private readonly Dictionary<string, SemaphoreSlim> _hostThrottles = new(); // 4 requests por host
    private readonly Dictionary<string, DateTime> _lastRequestByHost = new();
    private readonly TimeSpan _minDelayBetweenRequests = TimeSpan.FromMilliseconds(250); // 250ms entre requests al mismo host

    public ImprovedAvailabilityService(
        IServiceProvider serviceProvider,
        ILogger<ImprovedAvailabilityService> log,
        IConfiguration config,
        IVtexCookieManager cookieManager)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _config = config;
        _cookieManager = cookieManager;
        _sqlConn = config.GetConnectionString("Default")!;
    }

    /// <summary>
    /// 🚀 MÉTODO PRINCIPAL: Verificación completa con todas las mejoras
    /// </summary>
    public async Task<ComprehensiveResult> RunComprehensiveCheckAsync(
        string? specificHost = null,
        CancellationToken ct = default)
    {
        var result = new ComprehensiveResult { StartedAt = DateTime.UtcNow };

        _log.LogInformation("🚀 === INICIANDO VERIFICACIÓN COMPREHENSIVA MEJORADA ===");

        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();

            // 1️⃣ OBTENER CADENAS HABILITADAS
            var retailers = await GetEnabledRetailersAsync(db, specificHost, ct);
            result.TotalRetailers = retailers.Count;

            _log.LogInformation("🏢 Cadenas a procesar: {Count}", retailers.Count);

            // 2️⃣ OBTENER PRODUCTOS CON TRACK = TRUE ✅
            var trackedProducts = await GetTrackedProductsAsync(db, ct);
            result.TotalProductsToTrack = trackedProducts.Count;

            _log.LogInformation("📋 Productos con Track=true: {Count} ({AdecoCount} Adeco, {CompCount} competencia)",
                trackedProducts.Count,
                trackedProducts.Count(p => p.Owner == "Adeco"),
                trackedProducts.Count(p => p.Owner != "Adeco"));

            if (trackedProducts.Count == 0)
            {
                _log.LogWarning("⚠️ No hay productos con Track=true");
                result.ErrorMessage = "No hay productos para trackear";
                return result;
            }

            // 3️⃣ PROCESAR CADA CADENA
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

                // ⏰ PAUSA ENTRE CADENAS (crucial para no saturar)
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;

            // 4️⃣ REPORTE FINAL
            LogFinalReport(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error fatal en verificación comprehensiva");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// 🏢 Procesar una cadena específica
    /// </summary>
    private async Task<RetailerResult> ProcessRetailerAsync(
        RetailerInfo retailer,
        List<ProductToTrack> trackedProducts,
        CancellationToken ct)
    {
        var result = new RetailerResult { RetailerHost = retailer.VtexHost };
        var salesChannel = retailer.SalesChannels.First();

        // 🔧 INICIALIZAR THROTTLE PARA ESTE HOST
        if (!_hostThrottles.ContainsKey(retailer.VtexHost))
        {
            _hostThrottles[retailer.VtexHost] = new SemaphoreSlim(4, 4); // Max 4 requests simultáneos por host
        }

        // 🍪 CONFIGURAR COOKIES USANDO EL MANAGER (sin hardcodear)
        await SetupCookiesAsync(retailer.VtexHost, salesChannel);

        // 🌐 CREAR CLIENTE CON PROXY Y COOKIES
        using var httpClient = CreateHttpClientWithProxyAndCookies(retailer.VtexHost);

        // 🔍 OBTENER PRODUCTOS DISPONIBLES EN ESTA CADENA
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

        // 📍 OBTENER SUCURSALES
        var stores = await GetStoresForRetailerAsync(db, retailer.RetailerId, ct);

        if (stores.Count == 0)
        {
            _log.LogWarning("⚠️ {RetailerName}: No se encontraron sucursales",
                retailer.DisplayName);
            return result;
        }

        _log.LogInformation("📍 {RetailerName}: {StoreCount} sucursales para verificar",
            retailer.DisplayName, stores.Count);

        // 🚀 PROCESAR CON THROTTLING CONTROLADO
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

                // Control de memoria: procesar en batches
                if (tasks.Count >= 50)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }

            if (ct.IsCancellationRequested) break;
        }

        // Esperar tareas restantes
        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        return result;
    }

    /// <summary>
    /// 🎯 Procesar un producto en una sucursal CON THROTTLING
    /// </summary>
    private async Task ProcessProductStoreWithThrottlingAsync(
        HttpClient httpClient,
        RetailerInfo retailer,
        AvailableProduct product,
        StoreInfo store,
        int salesChannel,
        RetailerResult result,
        object resultsLock,
        CancellationToken ct)
    {
        // 🔐 THROTTLING GLOBAL
        await _globalThrottle.WaitAsync(ct);

        try
        {
            // 🔐 THROTTLING POR HOST
            await _hostThrottles[retailer.VtexHost].WaitAsync(ct);

            try
            {
                // ⏰ DELAY MÍNIMO ENTRE REQUESTS AL MISMO HOST
                await EnforceMinimumDelayAsync(retailer.VtexHost);

                // 🧪 TESTEAR DISPONIBILIDAD
                var availability = await TestAvailabilityWithRetryAsync(
                    httpClient, retailer.VtexHost, salesChannel, product, store, ct);

                // 💾 GUARDAR RESULTADO
                await SaveAvailabilityResultAsync(
                    retailer.VtexHost, store, product, availability, salesChannel, ct);

                lock (resultsLock)
                {
                    result.ProductChecks++;
                    if (availability.IsAvailable) result.AvailableProducts++;

                    // Log cada 100 verificaciones
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

    /// <summary>
    /// ⏰ Asegurar delay mínimo entre requests al mismo host
    /// </summary>
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
                    Thread.Sleep(delayNeeded); // Bloqueo sincrónico para este host
                }
            }

            _lastRequestByHost[host] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 🔄 Testear disponibilidad CON REINTENTOS
    /// </summary>
    private async Task<AvailabilityTestResult> TestAvailabilityWithRetryAsync(
        HttpClient httpClient,
        string host,
        int salesChannel,
        AvailableProduct product,
        StoreInfo store,
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
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    _log.LogWarning("⚠️ Intento {Attempt}/{Max} falló para {Product} en {Store}. Reintentando en {Delay}s...",
                        attempt, maxRetries, product.ProductName, store.StoreName, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }
            }
        }

        // Si llegamos aquí, todos los intentos fallaron
        return new AvailabilityTestResult
        {
            ProductEan = product.EAN,
            SkuId = product.SkuId,
            SellerId = product.SellerId,
            ErrorMessage = $"Falló después de {maxRetries} intentos: {lastException?.Message}"
        };
    }

    /// <summary>
    /// 🧪 Testear disponibilidad (lógica principal)
    /// </summary>
    private async Task<AvailabilityTestResult> TestAvailabilityAsync(
        HttpClient httpClient,
        string host,
        int salesChannel,
        AvailableProduct product,
        StoreInfo store,
        CancellationToken ct)
    {
        var result = new AvailabilityTestResult
        {
            ProductEan = product.EAN,
            SkuId = product.SkuId,
            SellerId = product.SellerId
        };

        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";

        var payload = new
        {
            items = new[] { new { id = product.SkuId, quantity = 1, seller = product.SellerId } },
            country = "AR",
            postalCode = store.PostalCode,
            shippingData = new
            {
                address = new
                {
                    addressType = store.VtexPickupPointId != null ? "pickup" : "residential",
                    country = "AR",
                    postalCode = store.PostalCode,
                    city = store.City,
                    state = store.Province
                },
                logisticsInfo = store.VtexPickupPointId != null ? new[]
                {
                    new
                    {
                        itemIndex = 0,
                        selectedSla = store.VtexPickupPointId,
                        selectedDeliveryChannel = "pickup-in-point"
                    }
                } : null
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
    /// <summary>
    /// 📋 Parsear respuesta de simulación - VERSIÓN CORREGIDA
    /// </summary>
    private static AvailabilityTestResult ParseSimulationResponse(AvailabilityTestResult result, string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            // 🔍 PASO 1: Verificar disponibilidad en items
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                var item = items[0];

                // ✅ CORRECCIÓN 1: Verificar availability correctamente
                if (item.TryGetProperty("availability", out var avail))
                {
                    var availStr = avail.GetString();
                    // "available" = disponible, "withoutStock" = sin stock, "cannotBeDelivered" = no se puede entregar
                    result.IsAvailable = availStr == "available";

                    if (availStr == "withoutStock")
                    {
                        result.ErrorMessage = "Sin stock";
                    }
                    else if (availStr == "cannotBeDelivered")
                    {
                        result.ErrorMessage = "No se puede entregar";
                    }
                }

                // ✅ CORRECCIÓN 2: Parse de precios (vienen en CENTAVOS)
                if (item.TryGetProperty("sellingPrice", out var sp) &&
                    sp.ValueKind == JsonValueKind.Number)
                {
                    var priceInCents = sp.GetDecimal();
                    result.Price = priceInCents / 100m; // Convertir centavos a pesos
                }

                if (item.TryGetProperty("listPrice", out var lp) &&
                    lp.ValueKind == JsonValueKind.Number)
                {
                    var listPriceInCents = lp.GetDecimal();
                    result.ListPrice = listPriceInCents / 100m; // Convertir centavos a pesos
                }

                // ✅ CORRECCIÓN 3: Cantidad disponible
                if (item.TryGetProperty("quantity", out var qty) &&
                    qty.ValueKind == JsonValueKind.Number)
                {
                    result.AvailableQuantity = qty.GetInt32();
                }
            }

            // 🔍 PASO 2: Verificar logística para confirmar disponibilidad
            // (Solo si hay SLAs disponibles, el producto realmente se puede entregar)
            if (doc.RootElement.TryGetProperty("logisticsInfo", out var logistics) &&
                logistics.ValueKind == JsonValueKind.Array &&
                logistics.GetArrayLength() > 0)
            {
                var logistic = logistics[0];

                if (logistic.TryGetProperty("slas", out var slas) &&
                    slas.ValueKind == JsonValueKind.Array)
                {
                    var slasCount = slas.GetArrayLength();

                    if (slasCount > 0)
                    {
                        // Si hay SLAs Y el item está marcado como "available", está disponible
                        // Si NO hay SLAs, NO está disponible (aunque diga "available")
                        // NO HACER NADA aquí si result.IsAvailable ya es true del paso anterior
                    }
                    else
                    {
                        // Sin SLAs = sin formas de entrega = NO disponible
                        result.IsAvailable = false;
                        if (string.IsNullOrEmpty(result.ErrorMessage))
                        {
                            result.ErrorMessage = "Sin opciones de entrega (SLAs vacíos)";
                        }
                    }
                }
            }

            // 🔍 PASO 3: Verificar mensajes de error
            if (doc.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() > 0)
            {
                var firstMessage = messages[0];

                if (firstMessage.TryGetProperty("text", out var msgText))
                {
                    var messageStr = msgText.GetString();

                    // Si hay mensaje de error, NO está disponible
                    if (firstMessage.TryGetProperty("status", out var status) &&
                        status.GetString() == "error")
                    {
                        result.IsAvailable = false;
                        result.ErrorMessage = messageStr ?? "Error desconocido";
                    }
                }
            }

            // 🔍 PASO 4: Moneda
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
    /// <summary>
    /// 🍪 Configurar cookies sin hardcodear
    /// </summary>
    private async Task SetupCookiesAsync(string host, int salesChannel)
    {
        _log.LogDebug("🍪 Configurando cookies para {Host} (SC: {SalesChannel})", host, salesChannel);

        // Warmup básico
        using var tempClient = CreateHttpClientWithProxyAndCookies(host);
        await _cookieManager.WarmupCookiesAsync(tempClient, host);

        // Actualizar segment cookie
        _cookieManager.UpdateSegmentCookie(host, salesChannel);
    }

    /// <summary>
    /// 🌐 Crear HttpClient con PROXY y COOKIES
    /// </summary>
    private HttpClient CreateHttpClientWithProxyAndCookies(string host)
    {
        var cookieContainer = _cookieManager.GetCookieContainer(host);

        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        // 🌐 CONFIGURAR PROXY
        var proxyUrl = _config["Proxy:Url"];
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            var proxy = new WebProxy(new Uri(proxyUrl));
            var username = _config["Proxy:Username"];
            if (!string.IsNullOrEmpty(username))
            {
                proxy.Credentials = new NetworkCredential(username, _config["Proxy:Password"]);
            }
            handler.Proxy = proxy;
            handler.UseProxy = true;

            _log.LogDebug("🌐 Usando proxy: {ProxyUrl}", proxyUrl);
        }

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");
        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }

    /// <summary>
    /// 🏢 Obtener cadenas habilitadas
    /// </summary>
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

    /// <summary>
    /// 📋 Obtener productos CON TRACK = TRUE ✅
    /// </summary>
    private async Task<List<ProductToTrack>> GetTrackedProductsAsync(AppDb db, CancellationToken ct)
    {
        return await db.ProductsToTrack
            .Where(p => p.Track.HasValue && p.Track.Value == true) // ✅ FILTRO CRÍTICO
            .AsNoTracking()
            .Select(p => new ProductToTrack
            {
                EAN = p.EAN,
                Owner = p.Owner,
                ProductName = p.ProductName
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// 🔍 Obtener productos disponibles en una cadena
    /// </summary>
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

    /// <summary>
    /// 📍 Obtener sucursales de una cadena
    /// </summary>
    private async Task<List<StoreInfo>> GetStoresForRetailerAsync(AppDb db, string retailerId, CancellationToken ct)
    {
        return await db.Stores
            .AsNoTracking()
            .Where(s => s.RetailerId == retailerId && s.IsActive)
            .Where(s => !string.IsNullOrEmpty(s.PostalCode))
            .Select(s => new StoreInfo
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

    /// <summary>
    /// 💾 Guardar resultado de disponibilidad
    /// </summary>
    private async Task SaveAvailabilityResultAsync(
        string host,
        StoreInfo store,
        AvailableProduct product,
        AvailabilityTestResult availability,
        int salesChannel,
        CancellationToken ct)
    {
        const string sql = @"
            MERGE dbo.VtexStoreAvailability AS T
            USING (VALUES(@host,@pp,@sku,@seller,@sc)) AS S (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel)
            ON (T.RetailerHost=S.RetailerHost AND T.PickupPointId=S.PickupPointId AND T.SkuId=S.SkuId AND T.SellerId=S.SellerId AND T.SalesChannel=S.SalesChannel)
            WHEN MATCHED THEN
              UPDATE SET IsAvailable=@avail, MaxFeasibleQty=@maxQty, Price=@price, Currency=@curr, CountryCode=@country, PostalCode=@postal, CapturedAtUtc=SYSUTCDATETIME(), RawJson=@raw, ErrorMessage=@error
            WHEN NOT MATCHED THEN
              INSERT (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel,CountryCode,PostalCode,IsAvailable,MaxFeasibleQty,Price,Currency,CapturedAtUtc,RawJson,ErrorMessage)
              VALUES (@host,@pp,@sku,@seller,@sc,@country,@postal,@avail,@maxQty,@price,@curr,SYSUTCDATETIME(),@raw,@error);";

        try
        {
            await using var connection = new SqlConnection(_sqlConn);
            await connection.OpenAsync(ct);

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@host", host);
            command.Parameters.AddWithValue("@pp", store.VtexPickupPointId ?? store.StoreId.ToString());
            command.Parameters.AddWithValue("@sku", product.SkuId);
            command.Parameters.AddWithValue("@seller", product.SellerId);
            command.Parameters.AddWithValue("@sc", salesChannel);
            command.Parameters.AddWithValue("@country", "AR");
            command.Parameters.AddWithValue("@postal", store.PostalCode);
            command.Parameters.AddWithValue("@avail", availability.IsAvailable);
            command.Parameters.AddWithValue("@maxQty", availability.AvailableQuantity);
            command.Parameters.AddWithValue("@price", (object?)availability.Price ?? DBNull.Value);
            command.Parameters.AddWithValue("@curr", availability.Currency ?? "ARS");
            command.Parameters.AddWithValue("@raw",
                (object?)availability.RawResponse?.Substring(0, Math.Min(availability.RawResponse.Length, 4000)) ?? DBNull.Value);
            command.Parameters.AddWithValue("@error", (object?)availability.ErrorMessage ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error guardando resultado para {Product} en {Store}",
                product.ProductName, store.StoreName);
        }
    }

    /// <summary>
    /// 📊 Log del reporte final
    /// </summary>
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

    #region DTOs

    private sealed class RetailerInfo
    {
        public string RetailerId { get; set; } = default!;
        public string DisplayName { get; set; } = default!;
        public string VtexHost { get; set; } = default!;
        public int[] SalesChannels { get; set; } = Array.Empty<int>();
        public int StoreCount { get; set; }
    }

    private sealed class ProductToTrack
    {
        public string EAN { get; set; } = default!;
        public string Owner { get; set; } = default!;
        public string? ProductName { get; set; }
    }

    private sealed class AvailableProduct
    {
        public string EAN { get; set; } = default!;
        public string SkuId { get; set; } = default!;
        public string SellerId { get; set; } = default!;
        public string ProductName { get; set; } = default!;
        public string Owner { get; set; } = default!;
    }

    private sealed class StoreInfo
    {
        public long StoreId { get; set; }
        public string StoreName { get; set; } = default!;
        public string City { get; set; } = default!;
        public string Province { get; set; } = default!;
        public string PostalCode { get; set; } = default!;
        public string? VtexPickupPointId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    private sealed class AvailabilityTestResult
    {
        public string ProductEan { get; set; } = default!;
        public string SkuId { get; set; } = default!;
        public string SellerId { get; set; } = default!;
        public bool IsAvailable { get; set; }
        public decimal? Price { get; set; }
        public decimal? ListPrice { get; set; }
        public int AvailableQuantity { get; set; }
        public string Currency { get; set; } = "ARS";
        public int StatusCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RawResponse { get; set; }
    }

    public sealed class RetailerResult
    {
        public string RetailerHost { get; set; } = default!;
        public int StoresProcessed { get; set; }
        public int ProductChecks { get; set; }
        public int AvailableProducts { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public sealed class ComprehensiveResult
    {
        public bool Success { get; set; }
        public int TotalRetailers { get; set; }
        public int TotalProductsToTrack { get; set; }
        public int TotalStoresProcessed { get; set; }
        public int TotalProductChecks { get; set; }
        public int TotalAvailableProducts { get; set; }
        public Dictionary<string, RetailerResult> RetailerResults { get; set; } = new();
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : TimeSpan.Zero;
        public string? ErrorMessage { get; set; }
    }

    #endregion
}