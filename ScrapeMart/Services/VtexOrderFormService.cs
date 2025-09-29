// File: Services/VtexOrderFormService.cs - VERSIÓN COMPLETA Y MODIFICADA
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Text;
using System.Text.Json;
using System.Net;

namespace ScrapeMart.Services;

/// <summary>
/// Servicio completo que verifica disponibilidad de TODOS los productos trackeados
/// en TODAS las sucursales de TODAS las cadenas habilitadas usando OrderForm
/// </summary>
public sealed class VtexOrderFormService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VtexOrderFormService> _log;
    private readonly string _sqlConn;
    private readonly IVtexCookieManager _cookieManager;

    public VtexOrderFormService(
        IServiceProvider serviceProvider,
        ILogger<VtexOrderFormService> log,
        IConfiguration cfg,
        IVtexCookieManager cookieManager)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
        _cookieManager = cookieManager;
    }

    /// <summary>
    /// 🚀 MÉTODO PRINCIPAL: Verifica disponibilidad completa
    /// </summary>
    public async Task ProbeAvailabilityWithOrderFormAsync(string? specificHost = null, CancellationToken ct = default)
    {
        _log.LogInformation("🚀 INICIANDO VERIFICACIÓN COMPLETA DE DISPONIBILIDAD");

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // 1️⃣ OBTENER TODAS LAS CADENAS HABILITADAS
        var enabledRetailers = await GetEnabledRetailersAsync(db, specificHost, ct);
        if (enabledRetailers.Count == 0)
        {
            _log.LogWarning("⚠️ No se encontraron cadenas habilitadas");
            return;
        }

        // 2️⃣ OBTENER TODOS LOS PRODUCTOS A TRACKEAR
        var productsToTrack = await GetProductsToTrackAsync(db, ct);
        if (productsToTrack.Count == 0)
        {
            _log.LogWarning("⚠️ No se encontraron productos para trackear");
            return;
        }

        _log.LogInformation("📊 DATOS CARGADOS:");
        _log.LogInformation("  🏢 Cadenas habilitadas: {RetailerCount}", enabledRetailers.Count);
        _log.LogInformation("  📋 Productos a trackear: {ProductCount}", productsToTrack.Count);
        _log.LogInformation("  📍 Total sucursales: {StoreCount}", enabledRetailers.Sum(r => r.StoreCount));

        var totalResults = new ComprehensiveAvailabilityResult();

        // 3️⃣ PROCESAR CADA CADENA
        foreach (var retailer in enabledRetailers)
        {
            if (ct.IsCancellationRequested) break;

            _log.LogInformation("🏢 === PROCESANDO {RetailerName} ({Host}) ===",
                retailer.DisplayName, retailer.VtexHost);

            try
            {
                var retailerResult = await ProcessRetailerCompleteAsync(retailer, productsToTrack, ct);
                totalResults.RetailerResults[retailer.VtexHost] = retailerResult;

                _log.LogInformation("✅ {RetailerName} completado: {Available}/{Total} productos disponibles",
                    retailer.DisplayName, retailerResult.AvailableProducts, retailerResult.TotalChecks);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "❌ Error procesando {RetailerName}", retailer.DisplayName);
                totalResults.RetailerResults[retailer.VtexHost] = new RetailerResult
                {
                    RetailerHost = retailer.VtexHost,
                    ErrorMessage = ex.Message
                };
            }

            // Pausa entre cadenas para evitar rate limiting
            await Task.Delay(3000, ct);
        }

        // 4️⃣ REPORTE FINAL
        LogFinalReport(totalResults);
    }

    /// <summary>
    /// 🏢 Procesar una cadena específica contra todos sus productos y sucursales
    /// </summary>
    private async Task<RetailerResult> ProcessRetailerCompleteAsync(
        RetailerInfo retailer,
        List<ProductToTrack> allProducts,
        CancellationToken ct)
    {
        var result = new RetailerResult { RetailerHost = retailer.VtexHost };
        var salesChannel = retailer.SalesChannels.First();

        // 🍪 CONFIGURAR COOKIES PARA ESTA CADENA
        await SetupCookiesForRetailer(retailer.VtexHost, salesChannel);

        // 🍪 CREAR CLIENTE CON COOKIES Y PROXY
        using var httpClient = CreateClientWithCookieManager(retailer.VtexHost);

        // 🔍 OBTENER PRODUCTOS QUE EXISTEN EN ESTA CADENA
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        var availableProducts = await GetAvailableProductsForRetailerAsync(db, retailer.VtexHost, allProducts, ct);

        if (availableProducts.Count == 0)
        {
            _log.LogWarning("⚠️ {RetailerName}: No se encontraron productos en catálogo", retailer.DisplayName);
            return result;
        }

        _log.LogInformation("📋 {RetailerName}: {Found}/{Total} productos encontrados en catálogo",
            retailer.DisplayName, availableProducts.Count, allProducts.Count);

        // 📍 OBTENER SUCURSALES DE ESTA CADENA
        var stores = await GetStoresForRetailerAsync(db, retailer.RetailerId, ct);

        if (stores.Count == 0)
        {
            _log.LogWarning("⚠️ {RetailerName}: No se encontraron sucursales", retailer.DisplayName);
            return result;
        }

        _log.LogInformation("📍 {RetailerName}: {StoreCount} sucursales para verificar",
            retailer.DisplayName, stores.Count);

        // 🎯 CONFIGURACIÓN DE PROCESAMIENTO
        var semaphore = new SemaphoreSlim(4, 4); // Máximo 4 requests paralelos por cadena
        var tasks = new List<Task>();
        var resultsLock = new object();

        // 🚀 PROCESAR CADA COMBINACIÓN PRODUCTO x SUCURSAL
        foreach (var product in availableProducts.Take(10)) // LIMITAR PARA TESTING - quitar Take en producción
        {
            foreach (var store in stores.Take(5)) // LIMITAR PARA TESTING - quitar Take en producción
            {
                if (ct.IsCancellationRequested) break;

                var task = ProcessProductStoreAsync(
                    httpClient, retailer, product, store, salesChannel,
                    semaphore, result, resultsLock, ct);

                tasks.Add(task);

                // Control de memoria - procesar en batches
                if (tasks.Count >= 20)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }
        }

        // Esperar tareas restantes
        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        return result;
    }

    /// <summary>
    /// 🧪 Verificar disponibilidad de un producto específico en una sucursal específica
    /// </summary>
    private async Task ProcessProductStoreAsync(
        HttpClient httpClient,
        RetailerInfo retailer,
        AvailableProduct product,
        StoreInfo store,
        int salesChannel,
        SemaphoreSlim semaphore,
        RetailerResult result,
        object resultsLock,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);

        try
        {
            _log.LogInformation("Consultando sucursal {StoreName} con CP {PostalCode} para producto {Product}", store.StoreName, store.PostalCode, product.ProductName);
            var availability = await TestProductAvailabilityAsync(
                httpClient, retailer.VtexHost, salesChannel, product, store, ct);

            // Guardar resultado en base de datos
            await SaveAvailabilityResultAsync(retailer.VtexHost, store, product, availability, salesChannel, ct);

            lock (resultsLock)
            {
                result.TotalChecks++;
                if (availability.IsAvailable) result.AvailableProducts++;

                if (result.TotalChecks % 50 == 0)
                {
                    _log.LogInformation("📊 {RetailerName}: {Completed} verificaciones completadas",
                        retailer.DisplayName, result.TotalChecks);
                }
            }

            if (availability.IsAvailable)
            {
                _log.LogDebug("✅ {Product} disponible en {Store} - ${Price:F2}",
                    product.ProductName, store.StoreName, availability.Price);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error verificando {Product} en {Store}",
                product.ProductName, store.StoreName);

            lock (resultsLock)
            {
                result.TotalChecks++;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// 🧪 Testear disponibilidad usando OrderForm
    /// </summary>
    private async Task<AvailabilityTestResult> TestProductAvailabilityAsync(
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

        try
        {
            // PASO 1: Crear OrderForm
            var orderFormId = await CreateOrderFormAsync(httpClient, host, salesChannel, ct);
            if (string.IsNullOrEmpty(orderFormId))
            {
                result.ErrorMessage = "No se pudo crear OrderForm";
                return result;
            }

            // PASO 2: Agregar producto al OrderForm
            var addResult = await AddItemToOrderFormAsync(httpClient, host, orderFormId, product, ct);
            if (!addResult.Success)
            {
                result.ErrorMessage = addResult.ErrorMessage;
                result.RawResponse = addResult.RawResponse;
                return result;
            }

            // PASO 3: Simular envío a la sucursal
            var simulationResult = await SimulateShippingToStoreAsync(
                httpClient, host, orderFormId, store, ct);

            if (simulationResult.Success)
            {
                result.IsAvailable = true;
                result.Price = simulationResult.Price;
                result.ListPrice = simulationResult.ListPrice;
                result.AvailableQuantity = simulationResult.AvailableQuantity;
                result.Currency = simulationResult.Currency;
                result.RawResponse = simulationResult.RawResponse;
            }
            else
            {
                result.ErrorMessage = simulationResult.ErrorMessage;
                result.RawResponse = simulationResult.RawResponse;
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 🛒 Crear OrderForm
    /// </summary>
    private async Task<string?> CreateOrderFormAsync(HttpClient httpClient, string host, int salesChannel, CancellationToken ct)
    {
        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm?sc={salesChannel}";

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            request.Headers.Add("Referer", host + "/");
            request.Headers.Add("x-requested-with", "XMLHttpRequest");

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);

            return doc.RootElement.TryGetProperty("orderFormId", out var idElement)
                ? idElement.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error creando OrderForm para {Host}", host);
            return null;
        }
    }

    /// <summary>
    /// ➕ Agregar item al OrderForm
    /// </summary>
    private async Task<AddItemResult> AddItemToOrderFormAsync(
        HttpClient httpClient,
        string host,
        string orderFormId,
        AvailableProduct product,
        CancellationToken ct)
    {
        var result = new AddItemResult();

        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}/items";

            var payload = new
            {
                orderItems = new[]
                {
                    new
                    {
                        id = product.SkuId,
                        quantity = 1,
                        seller = product.SellerId
                    }
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

            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"HTTP {response.StatusCode}";
                return result;
            }

            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                result.Success = true;
            }
            else
            {
                result.ErrorMessage = "No items in response";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 🚚 Simular envío a sucursal específica
    /// </summary>
    private async Task<ShippingSimulationResult> SimulateShippingToStoreAsync(
        HttpClient httpClient,
        string host,
        string orderFormId,
        StoreInfo store,
        CancellationToken ct)
    {
        var result = new ShippingSimulationResult();

        try
        {
            // Intentar con pickup point si está disponible
            if (!string.IsNullOrEmpty(store.VtexPickupPointId))
            {
                result = await SimulatePickupAsync(httpClient, host, orderFormId, store, ct);
                if (result.Success) return result;
            }

            // Fallback: simular delivery a la zona
            result = await SimulateDeliveryAsync(httpClient, host, orderFormId, store, ct);
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 📦 Simular pickup en punto específico
    /// </summary>
    private async Task<ShippingSimulationResult> SimulatePickupAsync(
        HttpClient httpClient,
        string host,
        string orderFormId,
        StoreInfo store,
        CancellationToken ct)
    {
        var result = new ShippingSimulationResult();

        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}/attachments/shippingData";

            var payload = new
            {
                address = new
                {
                    addressType = "pickup",
                    country = "ARG",
                    postalCode = store.PostalCode,
                    city = store.City,
                    state = store.Province
                },
                logisticsInfo = new[]
                {
                    new
                    {
                        itemIndex = 0,
                        selectedSla = store.VtexPickupPointId,
                        selectedDeliveryChannel = "pickup-in-point"
                    }
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

            if (response.IsSuccessStatusCode)
            {
                result = ParseShippingResponse(responseBody);
            }
            else
            {
                result.ErrorMessage = $"Pickup simulation failed: HTTP {response.StatusCode}";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 🚚 Simular delivery a zona
    /// </summary>
    private async Task<ShippingSimulationResult> SimulateDeliveryAsync(
        HttpClient httpClient,
        string host,
        string orderFormId,
        StoreInfo store,
        CancellationToken ct)
    {
        var result = new ShippingSimulationResult();

        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}/attachments/shippingData";

            var payload = new
            {
                address = new
                {
                    addressType = "residential",
                    country = "ARG",
                    postalCode = store.PostalCode,
                    city = store.City,
                    state = store.Province,
                    street = "Calle Test",
                    number = "123"
                },
                logisticsInfo = new[]
                {
                    new
                    {
                        itemIndex = 0,
                        selectedDeliveryChannel = "delivery"
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            request.Headers.Add("Referer", host + "/");

            using var response = await httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            result.RawResponse = responseBody;

            if (response.IsSuccessStatusCode)
            {
                result = ParseShippingResponse(responseBody);
            }
            else
            {
                result.ErrorMessage = $"Delivery simulation failed: HTTP {response.StatusCode}";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 📋 Parsear respuesta de simulación de envío
    /// </summary>
    private ShippingSimulationResult ParseShippingResponse(string responseBody)
    {
        var result = new ShippingSimulationResult { RawResponse = responseBody };

        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            // Verificar items para obtener precios y disponibilidad
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                var item = items[0];

                // Disponibilidad
                if (item.TryGetProperty("availability", out var avail) && avail.GetString() == "available")
                {
                    result.Success = true;
                }

                // Precios
                if (item.TryGetProperty("sellingPrice", out var sp) && sp.TryGetDecimal(out var sellingPrice))
                    result.Price = sellingPrice / 100m;

                if (item.TryGetProperty("listPrice", out var lp) && lp.TryGetDecimal(out var listPrice))
                    result.ListPrice = listPrice / 100m;

                // Cantidad disponible (si está en la respuesta)
                if (item.TryGetProperty("quantity", out var qty) && qty.TryGetInt32(out var quantity))
                    result.AvailableQuantity = quantity;
            }

            // Verificar logística para confirmar disponibilidad
            if (doc.RootElement.TryGetProperty("logisticsInfo", out var logistics) &&
                logistics.ValueKind == JsonValueKind.Array &&
                logistics.GetArrayLength() > 0)
            {
                var logistic = logistics[0];
                if (logistic.TryGetProperty("slas", out var slas) &&
                    slas.ValueKind == JsonValueKind.Array &&
                    slas.GetArrayLength() > 0)
                {
                    // Si hay SLAs disponibles, el producto se puede enviar
                    result.Success = true;
                }
            }

            // Moneda
            if (doc.RootElement.TryGetProperty("storePreferencesData", out var preferences) &&
                preferences.TryGetProperty("currencyCode", out var currency))
            {
                result.Currency = currency.GetString() ?? "ARS";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Parse error: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// 🏢 Obtener cadenas habilitadas
    /// </summary>
    private async Task<List<RetailerInfo>> GetEnabledRetailersAsync(AppDb db, string? specificHost, CancellationToken ct)
    {
        // Primero obtenemos los datos básicos que EF puede traducir a SQL
        var rawData = await (
            from retailer in db.Retailers.AsNoTracking()
            join config in db.VtexRetailersConfigs.AsNoTracking() on retailer.RetailerId equals config.RetailerId
            where config.Enabled && retailer.IsActive
            where (specificHost == null || config.RetailerHost == specificHost)
            select new
            {
                retailer.RetailerId,
                retailer.DisplayName,
                VtexHost = retailer.VtexHost ?? retailer.PublicHost!,
                SalesChannelsString = config.SalesChannels, // Mantenemos como string
                StoreCount = retailer.Stores.Count(s => s.IsActive)
            }
        ).ToListAsync(ct);

        // Después procesamos en memoria para hacer el Split y Parse
        var retailers = rawData.Select(r => new RetailerInfo
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

        // Log de cadenas encontradas
        foreach (var retailer in retailers)
        {
            _log.LogInformation("🏢 Cadena habilitada: {Name} ({Host}) - {StoreCount} sucursales - SC: [{SalesChannels}]",
                retailer.DisplayName, retailer.VtexHost, retailer.StoreCount,
                string.Join(",", retailer.SalesChannels));
        }

        return retailers;
    }

    /// <summary>
    /// 📋 Obtener productos a trackear
    /// </summary>
    private async Task<List<ProductToTrack>> GetProductsToTrackAsync(AppDb db, CancellationToken ct)
    {
        var products = await db.ProductsToTrack
            .Where(p => p.Track.HasValue && p.Track.Value == true)
            .AsNoTracking()
            .ToListAsync(ct); // Traemos todo a memoria primero

        // Ahora procesamos en memoria para crear la estructura que necesitamos
        var result = products.Select(p => new ProductToTrack
        {
            EAN = p.EAN,
            Owner = p.Owner,
            ProductName = p.ProductName
        }).ToList();

        _log.LogInformation("📋 Productos a trackear: {Total} ({Adeco} Adeco, {Others} otros)",
            result.Count,
            result.Count(p => p.Owner == "Adeco"),
            result.Count(p => p.Owner != "Adeco"));

        return result;
    }

    /// <summary>
    /// 🔍 Obtener productos que existen en el catálogo de una cadena
    /// </summary>
    private async Task<List<AvailableProduct>> GetAvailableProductsForRetailerAsync(
        AppDb db, string host, List<ProductToTrack> allProducts, CancellationToken ct)
    {
        var targetEans = allProducts.Select(p => p.EAN).ToList();

        // Crear un diccionario para lookup rápido de productos por EAN
        var productLookup = allProducts.ToDictionary(p => p.EAN, p => p);

        // Hacer la consulta SQL básica sin operaciones complejas
        var sellersData = await (
                from seller in db.Sellers.AsNoTracking()
                    // 👇👇👇 ACÁ USAMOS LA FUNCIÓN MAPEADA EN AMBOS LADOS 👇👇👇
                where MyDbFunctions.NormalizeHost(seller.Sku.RetailerHost) == MyDbFunctions.NormalizeHost(host)
                where seller.Sku.Ean != null && targetEans.Contains(seller.Sku.Ean)
                select new
                {
                    EAN = seller.Sku.Ean!,
                    SkuId = seller.Sku.ItemId,
                    seller.SellerId
                }
            ).Distinct().ToListAsync(ct);

        // Procesar en memoria para agregar información del producto
        var availableProducts = sellersData
            .Where(s => productLookup.ContainsKey(s.EAN)) // Por si acaso hay inconsistencias
            .Select(s => new AvailableProduct
            {
                EAN = s.EAN,
                SkuId = s.SkuId,
                SellerId = s.SellerId,
                ProductName = productLookup[s.EAN].ProductName ?? "Sin nombre",
                Owner = productLookup[s.EAN].Owner
            })
            .ToList();

        _log.LogInformation("🔍 {Host}: {Found}/{Total} productos encontrados en catálogo",
            host, availableProducts.Count, allProducts.Count);

        return availableProducts;
    }

    /// <summary>
    /// 📍 Obtener sucursales de una cadena
    /// </summary>
    private async Task<List<StoreInfo>> GetStoresForRetailerAsync(AppDb db, string retailerId, CancellationToken ct)
    {
        var stores = await db.Stores
            .AsNoTracking()
            .Where(s => s.RetailerId == retailerId && s.IsActive)
            .Where(s => !string.IsNullOrEmpty(s.PostalCode)) // Solo sucursales con código postal
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

        return stores;
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
            INSERT INTO dbo.AvailabilityResults 
            (RetailerHost, StoreId, ProductEAN, SkuId, SellerId, SalesChannel,
             IsAvailable, Price, ListPrice, AvailableQuantity, Currency, 
             ErrorMessage, RawResponse, CheckedAt)
            VALUES 
            (@host, @storeId, @ean, @skuId, @sellerId, @sc,
             @available, @price, @listPrice, @quantity, @currency,
             @error, @rawResponse, GETUTCDATE())";

        try
        {
            await using var connection = new SqlConnection(_sqlConn);
            await connection.OpenAsync(ct);

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@host", host);
            command.Parameters.AddWithValue("@storeId", store.StoreId);
            command.Parameters.AddWithValue("@ean", product.EAN);
            command.Parameters.AddWithValue("@skuId", product.SkuId);
            command.Parameters.AddWithValue("@sellerId", product.SellerId);
            command.Parameters.AddWithValue("@sc", salesChannel);
            command.Parameters.AddWithValue("@available", availability.IsAvailable);
            command.Parameters.AddWithValue("@price", (object?)availability.Price ?? DBNull.Value);
            command.Parameters.AddWithValue("@listPrice", (object?)availability.ListPrice ?? DBNull.Value);
            command.Parameters.AddWithValue("@quantity", availability.AvailableQuantity);
            command.Parameters.AddWithValue("@currency", availability.Currency ?? "ARS");
            command.Parameters.AddWithValue("@error", (object?)availability.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("@rawResponse",
                (object?)availability.RawResponse?.Substring(0, Math.Min(availability.RawResponse.Length, 4000)) ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error guardando resultado para {Product} en {Store}", product.ProductName, store.StoreName);
        }
    }


    /// <summary>
    /// 🍪 Configurar cookies usando el VtexCookieManager
    /// </summary>
    private async Task SetupCookiesForRetailer(string host, int salesChannel)
    {
        _log.LogInformation("🍪 Configurando cookies para {Host} (SC: {SalesChannel})", host, salesChannel);

        // El VtexCookieManager ya tiene toda la lógica de cookies específicas por cadena
        // Solo necesitamos hacer warmup y actualizar el segment cookie

        using var tempClient = CreateClientWithCookieManager(host);
        await _cookieManager.WarmupCookiesAsync(tempClient, host);

        // Actualizar segment cookie con sales channel correcto
        _cookieManager.UpdateSegmentCookie(host, salesChannel);

        _log.LogInformation("✅ Cookies configuradas para {Host}", host);
    }

    /// <summary>
    /// 🍪 Crear HttpClient con cookies del manager
    /// </summary>
    private HttpClient CreateClientWithCookieManager(string host)
    {
        var cookieContainer = _cookieManager.GetCookieContainer(host);
        var config = _serviceProvider.GetRequiredService<IConfiguration>();
        var proxyConfig = config.GetSection("Proxy");

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
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");
        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }


    /// <summary>
    /// 📊 Log del reporte final
    /// </summary>
    private void LogFinalReport(ComprehensiveAvailabilityResult totalResults)
    {
        _log.LogInformation("🎉 === REPORTE FINAL DE DISPONIBILIDAD ===");

        var totalChecks = totalResults.RetailerResults.Values.Sum(r => r.TotalChecks);
        var totalAvailable = totalResults.RetailerResults.Values.Sum(r => r.AvailableProducts);
        var successRate = totalChecks > 0 ? (double)totalAvailable / totalChecks : 0;

        _log.LogInformation("📊 ESTADÍSTICAS GENERALES:");
        _log.LogInformation("  🏢 Cadenas procesadas: {Retailers}", totalResults.RetailerResults.Count);
        _log.LogInformation("  🧪 Total verificaciones: {Total}", totalChecks);
        _log.LogInformation("  ✅ Productos disponibles: {Available}", totalAvailable);
        _log.LogInformation("  📈 Tasa de disponibilidad: {Rate:P2}", successRate);

        _log.LogInformation("📋 DESGLOSE POR CADENA:");
        foreach (var (host, result) in totalResults.RetailerResults)
        {
            var retailerRate = result.TotalChecks > 0 ? (double)result.AvailableProducts / result.TotalChecks : 0;
            _log.LogInformation("  🏢 {Host}: {Available}/{Total} ({Rate:P2})",
                host, result.AvailableProducts, result.TotalChecks, retailerRate);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                _log.LogWarning("    ❌ Error: {Error}", result.ErrorMessage);
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
        public string? ErrorMessage { get; set; }
        public string? RawResponse { get; set; }
    }

    private sealed class AddItemResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RawResponse { get; set; }
    }

    private sealed class ShippingSimulationResult
    {
        public bool Success { get; set; }
        public decimal? Price { get; set; }
        public decimal? ListPrice { get; set; }
        public int AvailableQuantity { get; set; }
        public string Currency { get; set; } = "ARS";
        public string? ErrorMessage { get; set; }
        public string? RawResponse { get; set; }
    }

    private sealed class RetailerResult
    {
        public string RetailerHost { get; set; } = default!;
        public int TotalChecks { get; set; }
        public int AvailableProducts { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed class ComprehensiveAvailabilityResult
    {
        public Dictionary<string, RetailerResult> RetailerResults { get; set; } = new();
    }

    #endregion
}