// File: Services/VtexOrderFormService.cs - CORREGIDO para usar VtexCookieManager
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Text;
using System.Text.Json;
using System.Net;

namespace ScrapeMart.Services;

/// <summary>
/// CORREGIDO: USA VtexCookieManager para flujo orderForm de todas las cadenas VTEX
/// </summary>
public sealed class VtexOrderFormService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VtexOrderFormService> _log;
    private readonly string _sqlConn;
    private readonly IVtexCookieManager _cookieManager; // 🍪 AGREGADO

    public VtexOrderFormService(
        IServiceProvider serviceProvider,
        ILogger<VtexOrderFormService> log,
        IConfiguration cfg,
        IVtexCookieManager cookieManager) // 🍪 INYECTADO
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
        _cookieManager = cookieManager; // 🍪 ASIGNADO
    }

    /// <summary>
    /// 🛒 MÉTODO CORREGIDO: Flujo completo orderForm usando VtexCookieManager
    /// </summary>
    public async Task ProbeAvailabilityWithOrderFormAsync(string host, CancellationToken ct)
    {
        _log.LogInformation("🛒 Iniciando flujo OrderForm con VtexCookieManager para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // 🔧 OBTENER CONFIG DE LA CADENA
        var config = await db.VtexRetailersConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RetailerHost == host && c.Enabled, ct);

        if (config == null)
        {
            _log.LogError("❌ No se encontró configuración para {Host}", host);
            return;
        }

        var salesChannel = int.Parse(config.SalesChannels.Split(',').First());

        // 🍪 CONFIGURAR COOKIES PARA ESTE HOST
        await SetupCookiesForHost(host, salesChannel);

        // 🍪 CREAR CLIENTE CON COOKIES DEL MANAGER
        using var httpClient = CreateClientWithCookieManager(host);

        // 📋 OBTENER PRODUCTOS A TESTEAR
        var targetEans = await db.ProductsToTrack.AsNoTracking()
            .Select(p => p.EAN)
            .Take(2) // Solo 2 para testing
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
            .Take(1) // Solo 1 SKU para testing del flujo completo
            .ToListAsync(ct);

        if (availableSkus.Count == 0)
        {
            _log.LogWarning("⚠️ No se encontraron SKUs para testear en {Host}", host);
            return;
        }

        // 🧪 FLUJO COMPLETO PARA CADA SKU
        foreach (var sku in availableSkus)
        {
            _log.LogInformation("🧪 Testing flujo completo OrderForm: {Product}", sku.ProductName);

            try
            {
                var result = await TestCompleteOrderFormFlowAsync(httpClient, host, salesChannel, sku, ct);
                await SaveAvailabilityResultAsync(host, sku, "test-orderform", salesChannel, result, ct);

                if (result.IsAvailable)
                {
                    _log.LogInformation("🎉 ¡FLUJO EXITOSO! {Product} disponible - ${Price:F2}", sku.ProductName, result.Price);
                }
                else
                {
                    _log.LogError("❌ Flujo falló para {Product}: {Error}", sku.ProductName, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "💥 Error en flujo OrderForm para {Product}", sku.ProductName);
            }
        }

        _log.LogInformation("🎉 Flujo OrderForm completado para {Host}", host);
    }

    /// <summary>
    /// 🍪 Configurar cookies para este host específico (REUTILIZA LÓGICA DEL SIMULATION SERVICE)
    /// </summary>
    private async Task SetupCookiesForHost(string host, int salesChannel)
    {
        if (host.Contains("vea.com.ar"))
        {
            // 🍪 COOKIES DE VEA (las mismas que pusiste)
            var veaCookies = "*vwo*uuid_v2=D141114DDECB39C78FDE0DA2F48546747|2ee47531154585f40ac2bd7c29301e2c; vtex-search-anonymous=5fc9a7ff55844ebeae13ae66986ae76a; checkout.vtex.com=__ofid=938ebb0c94c34808a9e9839b2093bb88; vtex_binding_address=veaargentina.myvtex.com/; *vwo*uuid=DDE37222879064FD7ABC4F579080F9076; *vis*opt_s=1%7C; locale=es-AR; VtexWorkspace=master%3A-; *fbp=fb.2.1758660348137.763496486246645936.Bg; VtexIdclientAutCookie*veaargentina=eyJhbGciOiJFUzI1NiIsImtpZCI6IjYwNzhCMDRGQUNDM0YyREU5NDhBN0IzRDJCOUZCRTg5OTI1Mzg3QjEiLCJ0eXAiOiJqd3QifQ.eyJzdWIiOiJhZGFuZ2Vsb3JAZ21haWwuY29tIiwiYWNjb3VudCI6InZlYWFyZ2VudGluYSIsImF1ZGllbmNlIjoid2Vic3RvcmUiLCJzZXNzIjoiZDJiMDBmOGYtZGIwNy00OWMyLTliNzctOTFhNTQ4YjlkNWYyIiwiZXhwIjoxNzU4NzQ2NzU3LCJ0eXBlIjoidXNlciIsInVzZXJJZCI6ImE4ZmRhZGYwLTBkMTItNGVkYy1hNjkzLWY5OGI0MTQ4ZWFkMiIsImlhdCI6MTc1ODY2MDM1NywiaXNSZXByZXNlbnRhdGl2ZSI6ZmFsc2UsImlzcyI6InRva2VuLWVtaXR0ZXIiLCJqdGkiOiJhNWQ4MWE2Ni1mMzMzLTQ4MzUtYWMzNC0zYjVmZTQ2N2MzZGYifQ.cbPwTE4bg35oigiKLy1LZY5peYVSHYmJkTUzl37l9au7pzr5-Gb5v6BFIP33MDzdfWvcLAJcabLvqIeEZht57w; VtexIdclientAutCookie_1e29887f-4d43-484f-b512-2013f42600b1=eyJhbGciOiJFUzI1NiIsImtpZCI6IjYwNzhCMDRGQUNDM0YyREU5NDhBN0IzRDJCOUZCRTg5OTI1Mzg3QjEiLCJ0eXAiOiJqd3QifQ.eyJzdWIiOiJhZGFuZ2Vsb3JAZ21haWwuY29tIiwiYWNjb3VudCI6InZlYWFyZ2VudGluYSIsImF1ZGllbmNlIjoid2Vic3RvcmUiLCJzZXNzIjoiZDJiMDBmOGYtZGIwNy00OWMyLTliNzctOTFhNTQ4YjlkNWYyIiwiZXhwIjoxNzU4NzQ2NzU3LCJ0eXBlIjoidXNlciIsInVzZXJJZCI6ImE4ZmRhZGYwLTBkMTItNGVkYy1hNjkzLWY5OGI0MTQ4ZWFkMiIsImlhdCI6MTc1ODY2MDM1NywiaXNSZXByZXNlbnRhdGl2ZSI6ZmFsc2UsImlzcyI6InRva2VuLWVtaXR0ZXIiLCJqdGkiOiJhNWQ4MWE2Ni1mMzMzLTQ4MzUtYWMzNC0zYjVmZTQ2N2MzZGYifQ.cbPwTE4bg35oigiKLy1LZY5peYVSHYmJkTUzl37l9au7pzr5-Gb5v6BFIP33MDzdfWvcLAJcabLvqIeEZht57w; vtex_session=eyJhbGciOiJFUzI1NiIsImtpZCI6IjhlYjAwZGVkLTJiZTYtNDU5My1hOTM1LTc0M2U5ZGI5ZTJkZSIsInR5cCI6IkpXVCJ9.eyJhY2NvdW50LmlkIjpbXSwiaWQiOiI5OTIyNDY3Zi1kZmVjLTRhZDgtODc5Zi03NjNlNzM5ZTBmZDMiLCJ2ZXJzaW9uIjo0LCJzdWIiOiJzZXNzaW9uIiwiYWNjb3VudCI6InNlc3Npb24iLCJleHAiOjE3NTkzNTE1ODQsImlhdCI6MTc1ODY2MDM4NCwianRpIjoiMGJhMTY2YmYtYmExYS00Y2ZmLWE0NGItNjdmMWU5MzRlNGQwIiwiaXNzIjoic2Vzc2lvbi9kYXRhLXNpZ25lciJ9.ABYU09YDZfGphniwhtUVF8sZBXOlx2LMWiORDwtSuhdgXPrX1NOFa039zaNNtxogfS-V30_5VjkkvkpWRjyZTA; vtex_segment=eyJjYW1wYWlnbnMiOm51bGwsImNoYW5uZWwiOiIzNCIsInByaWNlVGFibGVzIjpudWxsLCJyZWdpb25JZCI6IlUxY2phblZ0WW05aGNtZGxiblJwYm1GMk9EWXdjMkZ1YkhWcGN3PT0iLCJ1dG1fY2FtcGFpZ24iOm51bGwsInV0bV9zb3VyY2UiOm51bGwsInV0bWlfY2FtcGFpZ24iOm51bGwsImN1cnJlbmN5Q29kZSI6IkFSUyIsImN1cnJlbmN5U3ltYm9sIjoiJCIsImNvdW50cnlDb2RlIjoiQVJHIiwiY3VsdHVyZUluZm8iOiJlcy1BUiIsImFkbWluX2N1bHR1cmVJbmZvIjoiZXMtQVIiLCJjaGFubmVsUHJpdmFjeSI6InB1YmxpYyJ9; vtex-search-session=5f9a75f8c4aa446a9ae9d8666a223a19; CheckoutOrderFormOwnership=";

            _cookieManager.SetCookiesForHost(host, veaCookies);
            _log.LogInformation("🍪 Cookies configuradas para VEA");
        }
        else if (host.Contains("jumbo.com.ar"))
        {
            // TODO: Configurar cookies de Jumbo cuando las tengas
            _log.LogInformation("⚠️ Cookies de Jumbo no configuradas, usando defaults");
        }
        else if (host.Contains("disco.com.ar"))
        {
            // TODO: Configurar cookies de Disco cuando las tengas
            _log.LogInformation("⚠️ Cookies de Disco no configuradas, usando defaults");
        }
        // ... más cadenas según tengas las cookies

        // 🔧 ACTUALIZAR SEGMENT COOKIE CON EL SALES CHANNEL CORRECTO
        _cookieManager.UpdateSegmentCookie(host, salesChannel);
    }

    /// <summary>
    /// 🍪 Crear HttpClient usando el VtexCookieManager
    /// </summary>
    private HttpClient CreateClientWithCookieManager(string host)
    {
        // 🍪 OBTENER COOKIES DEL MANAGER
        var cookieContainer = _cookieManager.GetCookieContainer(host);

        var handler = new HttpClientHandler()
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler);

        // 🔧 HEADERS COMO NAVEGADOR REAL
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");

        client.Timeout = TimeSpan.FromSeconds(30);

        _log.LogInformation("🍪 Cliente HTTP creado con cookies del VtexCookieManager para {Host}", host);
        return client;
    }

    /// <summary>
    /// 🧪 Flujo completo OrderForm: crear → agregar item → simular
    /// </summary>
    private async Task<AvailabilityResult> TestCompleteOrderFormFlowAsync(
        HttpClient httpClient, string host, int salesChannel, SkuToTest sku, CancellationToken ct)
    {
        var result = new AvailabilityResult();

        try
        {
            // PASO 1: Crear orderForm
            _log.LogInformation("PASO 1: Creando OrderForm...");
            var orderFormId = await CreateOrderFormAsync(httpClient, host, salesChannel, ct);
            if (string.IsNullOrEmpty(orderFormId))
            {
                result.ErrorMessage = "No se pudo crear orderForm";
                return result;
            }

            _log.LogInformation("✅ OrderForm creado: {OrderFormId}", orderFormId);

            // PASO 2: Agregar item
            _log.LogInformation("PASO 2: Agregando item {SkuId}...", sku.SkuId);
            var addResult = await AddItemToOrderFormAsync(httpClient, host, orderFormId, sku, ct);
            if (!addResult.Success)
            {
                result.ErrorMessage = $"No se pudo agregar item: {addResult.Error}";
                result.RawResponse = addResult.RawResponse;
                return result;
            }

            _log.LogInformation("✅ Item agregado correctamente");

            // PASO 3: Simular (opcional - el item ya está en el orderForm)
            _log.LogInformation("PASO 3: Verificando orderForm final...");
            var finalResult = await GetOrderFormAsync(httpClient, host, orderFormId, ct);
            return ParseFinalOrderForm(finalResult);

        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// PASO 1: Crear orderForm usando cookies del manager
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
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _log.LogInformation("📊 CreateOrderForm: {Status} - Preview: {Preview}",
                response.StatusCode,
                responseBody.Length > 200 ? responseBody.Substring(0, 200) + "..." : responseBody);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("orderFormId", out var idElement))
            {
                return idElement.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error creating OrderForm");
            return null;
        }
    }

    /// <summary>
    /// PASO 2: Agregar item al orderForm
    /// </summary>
    private async Task<(bool Success, string? Error, string? RawResponse)> AddItemToOrderFormAsync(
        HttpClient httpClient, string host, string orderFormId, SkuToTest sku, CancellationToken ct)
    {
        try
        {
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

            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                return (true, null, responseBody);
            }

            return (false, "No items in response", responseBody);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// PASO 3: Obtener orderForm final
    /// </summary>
    private async Task<string> GetOrderFormAsync(HttpClient httpClient, string host, string orderFormId, CancellationToken ct)
    {
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Referer", host + "/");

        using var response = await httpClient.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Parsear orderForm final para determinar disponibilidad
    /// </summary>
    private AvailabilityResult ParseFinalOrderForm(string responseBody)
    {
        var result = new AvailabilityResult { RawResponse = responseBody };

        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            // Verificar items
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                var item = items[0];

                if (item.TryGetProperty("availability", out var avail))
                {
                    var availability = avail.GetString();
                    if (availability == "available")
                    {
                        result.IsAvailable = true;

                        if (item.TryGetProperty("sellingPrice", out var sp) && sp.TryGetDecimal(out var spDecimal))
                            result.Price = spDecimal / 100m;

                        if (item.TryGetProperty("listPrice", out var lp) && lp.TryGetDecimal(out var lpDecimal))
                            result.ListPrice = lpDecimal / 100m;
                    }
                    else
                    {
                        result.ErrorMessage = $"Item availability: {availability}";
                    }
                }
            }
            else
            {
                result.ErrorMessage = "No items in orderForm";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Parse error: {ex.Message}";
            return result;
        }
    }

    // Guardar resultado (igual que antes)
    private async Task SaveAvailabilityResultAsync(string host, SkuToTest sku, string pickupPointId, int salesChannel, AvailabilityResult result, CancellationToken ct)
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
        cmd.Parameters.AddWithValue("@pp", pickupPointId);
        cmd.Parameters.AddWithValue("@sku", sku.SkuId);
        cmd.Parameters.AddWithValue("@seller", sku.SellerId);
        cmd.Parameters.AddWithValue("@sc", salesChannel);
        cmd.Parameters.AddWithValue("@country", "AR");
        cmd.Parameters.AddWithValue("@postal", "");
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