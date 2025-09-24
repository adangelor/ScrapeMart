// File: Services/VtexOrderFormService.cs
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// Servicio que usa el flujo real de orderForm de VTEX como lo hace la web
/// </summary>
public sealed class VtexOrderFormService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VtexOrderFormService> _log;
    private readonly string _sqlConn;

    public VtexOrderFormService(IServiceProvider serviceProvider, ILogger<VtexOrderFormService> log, IConfiguration cfg)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
    }

    /// <summary>
    /// Flujo completo: Crear orderForm → Agregar items → Simular shipping → Verificar disponibilidad
    /// </summary>
    public async Task ProbeAvailabilityWithOrderFormAsync(string host, CancellationToken ct)
    {
        _log.LogInformation("🛒 Iniciando flujo REAL de OrderForm para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // 🆕 Usar el proxy service en lugar del session service problemático
        var proxyService = scope.ServiceProvider.GetRequiredService<VtexProxyService>();

        // 1. Obtener configuración y datos base
        var config = await db.VtexRetailersConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RetailerHost == host && c.Enabled, ct);

        if (config == null)
        {
            _log.LogError("❌ No se encontró configuración para {Host}", host);
            return;
        }

        var salesChannel = int.Parse(config.SalesChannels.Split(',').First());

        // 🆕 Usar HttpClient del proxy service que sabemos que funciona
        using var httpClient = proxyService.CreateProxyClient();

        _log.LogInformation("✅ Cliente HTTP configurado para {Host}. Sales Channel: {SC}", host, salesChannel);

        // 2. Obtener productos a testear
        var targetEans = await db.ProductsToTrack.AsNoTracking()
            .Select(p => p.EAN)
            .ToListAsync(ct);

        var availableSkus = await db.Sellers.AsNoTracking()
            .Where(s => s.Sku.RetailerHost == host &&
                       s.Sku.Ean != null &&
                       targetEans.Contains(s.Sku.Ean))
            .Select(s => new SkuToTest
            {
                SkuId = s.Sku.ItemId,
                SellerId = s.SellerId,
                Ean = s.Sku.Ean!,
                ProductName = s.Sku.Product.ProductName ?? "Sin nombre"
            })
            .Distinct()
            .Take(10) // Limitamos para testing
            .ToListAsync(ct);

        // 3. Obtener sucursales
        var storeLocations = await db.VtexPickupPoints.AsNoTracking()
            .Where(pp => pp.RetailerHost == host && pp.SourceIdSucursal.HasValue)
            .Join(db.Sucursales.AsNoTracking(),
                  pp => new { B = pp.SourceIdBandera!.Value, C = pp.SourceIdComercio!.Value, S = pp.SourceIdSucursal.Value },
                  s => new { B = s.IdBandera, C = s.IdComercio, S = s.IdSucursal },
                  (pp, s) => new StoreLocation
                  {
                      PickupPointId = pp.PickupPointId,
                      PostalCode = s.SucursalesCodigoPostal,
                      StoreName = s.SucursalesNombre
                  })
            .Take(5) // Limitamos para testing
            .ToListAsync(ct);

        _log.LogInformation("📊 Testing: {SkuCount} SKUs × {StoreCount} stores = {Total} combinaciones",
            availableSkus.Count, storeLocations.Count, availableSkus.Count * storeLocations.Count);

        // ⚠️ Verificación: Si no hay stores, no podemos testear
        if (storeLocations.Count == 0)
        {
            _log.LogWarning("❌ No se encontraron pickup points mapeados para {Host}. Ejecuta primero el sweep de stores.", host);
            _log.LogInformation("💡 Sugerencia: POST /api/operations/stores/sweep?hostFilter={Host}", Uri.EscapeDataString(host));
            return;
        }

        // 4. Para cada combinación SKU + Store, hacer el flujo completo
        foreach (var sku in availableSkus)
        {
            foreach (var store in storeLocations)
            {
                _log.LogInformation("🧪 Testing: {Product} en {Store}", sku.ProductName, store.StoreName);

                try
                {
                    var result = await TestSkuInStoreWithOrderFormAsync(
                        httpClient, host, salesChannel, sku, store, ct);

                    await SaveAvailabilityResultAsync(host, sku, store, salesChannel, result, ct);

                    if (result.IsAvailable)
                    {
                        _log.LogInformation("✅ {Product} DISPONIBLE en {Store} - ${Price:F2}",
                            sku.ProductName, store.StoreName, result.Price);
                    }
                    else
                    {
                        _log.LogInformation("❌ {Product} NO DISPONIBLE en {Store} - {Error}",
                            sku.ProductName, store.StoreName, result.ErrorMessage);
                    }

                    // Pausa para no saturar la API
                    await Task.Delay(200, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "💥 Error en flujo OrderForm para {Product} en {Store}",
                        sku.ProductName, store.StoreName);
                }
            }
        }

        _log.LogInformation("🎉 Flujo OrderForm completado para {Host}", host);
    }

    /// <summary>
    /// Flujo completo de OrderForm para un SKU en una sucursal específica
    /// </summary>
    private async Task<AvailabilityResult> TestSkuInStoreWithOrderFormAsync(
        HttpClient httpClient,
        string host,
        int salesChannel,
        SkuToTest sku,
        StoreLocation store,
        CancellationToken ct)
    {
        try
        {
            // PASO 1: Crear un orderForm vacío
            var orderFormId = await CreateOrderFormAsync(httpClient, host, salesChannel, ct);
            if (string.IsNullOrEmpty(orderFormId))
            {
                return new AvailabilityResult
                {
                    IsAvailable = false,
                    ErrorMessage = "No se pudo crear orderForm"
                };
            }

            _log.LogDebug("📝 OrderForm creado: {OrderFormId}", orderFormId);

            // PASO 2: Agregar el item al orderForm
            var addItemResult = await AddItemToOrderFormAsync(httpClient, host, orderFormId, sku, ct);
            if (!addItemResult.Success)
            {
                return new AvailabilityResult
                {
                    IsAvailable = false,
                    ErrorMessage = $"No se pudo agregar item: {addItemResult.Error}",
                    RawResponse = addItemResult.RawResponse
                };
            }

            _log.LogDebug("➕ Item agregado al orderForm");

            // PASO 3: Simular shipping para la sucursal específica
            var shippingResult = await SimulateShippingAsync(httpClient, host, orderFormId, store, ct);
            if (!shippingResult.Success)
            {
                return new AvailabilityResult
                {
                    IsAvailable = false,
                    ErrorMessage = $"Shipping simulation failed: {shippingResult.Error}",
                    RawResponse = shippingResult.RawResponse
                };
            }

            _log.LogDebug("🚚 Shipping simulado");

            // PASO 4: Verificar el orderForm final para confirmar disponibilidad
            var finalResult = await GetOrderFormAsync(httpClient, host, orderFormId, ct);
            return ParseFinalOrderForm(finalResult, store.PickupPointId);
        }
        catch (Exception ex)
        {
            return new AvailabilityResult
            {
                IsAvailable = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// PASO 1: Crear orderForm vacío
    /// </summary>
    private async Task<string?> CreateOrderFormAsync(HttpClient httpClient, string host, int salesChannel, CancellationToken ct)
    {
        // ✅ URL CORREGIDA: /orderForm SIN la S
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm?sc={salesChannel}";

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        request.Headers.Add("Referer", host + "/");
        request.Headers.Add("x-requested-with", "XMLHttpRequest");

        using var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("❌ Failed to create orderForm: HTTP {Status} - {Body}", response.StatusCode, responseBody);
            return null;
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("orderFormId", out var idElement))
        {
            return idElement.GetString();
        }

        return null;
    }

    /// <summary>
    /// PASO 2: Agregar item al orderForm
    /// </summary>
    private async Task<(bool Success, string? Error, string? RawResponse)> AddItemToOrderFormAsync(
        HttpClient httpClient, string host, string orderFormId, SkuToTest sku, CancellationToken ct)
    {
        // ✅ URL CORREGIDA: /orderForm SIN la S
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}/items";

        var payload = new
        {
            orderItems = new[]
            {
                new
                {
                    id = sku.SkuId,
                    quantity = 1,
                    seller = sku.SellerId
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

        if (!response.IsSuccessStatusCode)
        {
            return (false, $"HTTP {response.StatusCode}", responseBody);
        }

        // Verificar que el item se agregó correctamente
        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array &&
            items.GetArrayLength() > 0)
        {
            var item = items[0];
            if (item.TryGetProperty("availability", out var avail))
            {
                var availability = avail.GetString();
                if (availability != "available")
                {
                    return (false, $"Item not available: {availability}", responseBody);
                }
            }
            return (true, null, responseBody);
        }

        return (false, "No items in response", responseBody);
    }

    /// <summary>
    /// PASO 3: Simular shipping para pickup en sucursal específica
    /// </summary>
    private async Task<(bool Success, string? Error, string? RawResponse)> SimulateShippingAsync(
        HttpClient httpClient, string host, string orderFormId, StoreLocation store, CancellationToken ct)
    {
        // ✅ URL CORREGIDA: /orderForm SIN la S
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}/attachments/shippingData";

        var payload = new
        {
            address = new
            {
                addressType = "pickup",
                country = "AR",
                postalCode = store.PostalCode
            },
            logisticsInfo = new[]
            {
                new
                {
                    itemIndex = 0,
                    selectedSla = store.PickupPointId,
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

        if (!response.IsSuccessStatusCode)
        {
            return (false, $"HTTP {response.StatusCode}", responseBody);
        }

        return (true, null, responseBody);
    }

    /// <summary>
    /// PASO 4: Obtener orderForm final
    /// </summary>
    private async Task<string> GetOrderFormAsync(HttpClient httpClient, string host, string orderFormId, CancellationToken ct)
    {
        // ✅ URL CORREGIDA: /orderForm SIN la S
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Referer", host + "/");

        using var response = await httpClient.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Parsear el orderForm final para determinar disponibilidad real
    /// </summary>
    private AvailabilityResult ParseFinalOrderForm(string responseBody, string expectedPickupPointId)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var result = new AvailabilityResult { RawResponse = responseBody };

            // 1. Verificar items
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                var item = items[0];

                // Verificar availability
                if (item.TryGetProperty("availability", out var avail))
                {
                    var availability = avail.GetString();
                    if (availability != "available")
                    {
                        result.IsAvailable = false;
                        result.ErrorMessage = $"Item availability: {availability}";
                        return result;
                    }
                }

                // Extraer precios
                if (item.TryGetProperty("sellingPrice", out var sp) && sp.TryGetDecimal(out var spDecimal))
                    result.Price = spDecimal / 100m;

                if (item.TryGetProperty("listPrice", out var lp) && lp.TryGetDecimal(out var lpDecimal))
                    result.ListPrice = lpDecimal / 100m;
            }

            // 2. Verificar shipping/logistics
            if (doc.RootElement.TryGetProperty("shippingData", out var shipping) &&
                shipping.TryGetProperty("logisticsInfo", out var logistics) &&
                logistics.ValueKind == JsonValueKind.Array &&
                logistics.GetArrayLength() > 0)
            {
                var logisticInfo = logistics[0];

                if (logisticInfo.TryGetProperty("slas", out var slas) && slas.ValueKind == JsonValueKind.Array)
                {
                    bool foundValidPickup = false;

                    foreach (var sla in slas.EnumerateArray())
                    {
                        if (sla.TryGetProperty("id", out var slaId) &&
                            slaId.GetString() == expectedPickupPointId &&
                            sla.TryGetProperty("deliveryChannel", out var dc) &&
                            dc.GetString() == "pickup-in-point")
                        {
                            // Verificar que no tenga errores
                            if (sla.TryGetProperty("pickupStoreInfo", out var storeInfo))
                            {
                                if (storeInfo.TryGetProperty("isPickupStore", out var isPickup) &&
                                    isPickup.GetBoolean())
                                {
                                    foundValidPickup = true;
                                    break;
                                }
                            }
                            else
                            {
                                // Si no hay pickupStoreInfo pero el SLA está presente, asumimos que es válido
                                foundValidPickup = true;
                                break;
                            }
                        }
                    }

                    result.IsAvailable = foundValidPickup;
                    if (!foundValidPickup)
                    {
                        result.ErrorMessage = $"Pickup point {expectedPickupPointId} not available or not found";
                    }
                }
                else
                {
                    result.IsAvailable = false;
                    result.ErrorMessage = "No SLAs available";
                }
            }
            else
            {
                result.IsAvailable = false;
                result.ErrorMessage = "No shipping data available";
            }

            // 3. Verificar si hay mensajes de error generales
            if (doc.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() > 0)
            {
                var errorMessages = new List<string>();
                foreach (var msg in messages.EnumerateArray())
                {
                    if (msg.TryGetProperty("text", out var text))
                    {
                        errorMessages.Add(text.GetString() ?? "Unknown error");
                    }
                }

                if (errorMessages.Any())
                {
                    result.IsAvailable = false;
                    result.ErrorMessage = string.Join("; ", errorMessages);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return new AvailabilityResult
            {
                IsAvailable = false,
                ErrorMessage = $"Parse error: {ex.Message}",
                RawResponse = responseBody
            };
        }
    }

    /// <summary>
    /// Guardar resultado en BD
    /// </summary>
    private async Task SaveAvailabilityResultAsync(
        string host,
        SkuToTest sku,
        StoreLocation store,
        int salesChannel,
        AvailabilityResult result,
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

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);

        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@pp", store.PickupPointId);
        cmd.Parameters.AddWithValue("@sku", sku.SkuId);
        cmd.Parameters.AddWithValue("@seller", sku.SellerId);
        cmd.Parameters.AddWithValue("@sc", salesChannel);
        cmd.Parameters.AddWithValue("@country", "AR");
        cmd.Parameters.AddWithValue("@postal", store.PostalCode);
        cmd.Parameters.AddWithValue("@avail", result.IsAvailable);
        cmd.Parameters.AddWithValue("@maxQty", result.IsAvailable ? 1 : 0);
        cmd.Parameters.AddWithValue("@price", (object?)result.Price ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@curr", result.Currency ?? "ARS");
        cmd.Parameters.AddWithValue("@raw", (object?)result.RawResponse?.Substring(0, Math.Min(result.RawResponse.Length, 4000)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)result.ErrorMessage ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    #region DTOs
    private sealed class SkuToTest
    {
        public string SkuId { get; set; } = default!;
        public string SellerId { get; set; } = default!;
        public string Ean { get; set; } = default!;
        public string ProductName { get; set; } = default!;
    }

    private sealed class StoreLocation
    {
        public string PickupPointId { get; set; } = default!;
        public string PostalCode { get; set; } = default!;
        public string StoreName { get; set; } = default!;
    }

    private sealed class AvailabilityResult
    {
        public bool IsAvailable { get; set; }
        public decimal? Price { get; set; }
        public decimal? ListPrice { get; set; }
        public string Currency { get; set; } = "ARS";
        public string? ErrorMessage { get; set; }
        public string? RawResponse { get; set; }
    }
    #endregion
}