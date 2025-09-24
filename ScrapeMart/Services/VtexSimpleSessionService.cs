// File: Services/VtexSimpleSessionService.cs
using System.Net;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// Versión SIMPLE que usa GET y URLs correctas (sin S)
/// </summary>
public sealed class VtexSimpleSessionService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<VtexSimpleSessionService> _log;

    public VtexSimpleSessionService(IHttpClientFactory httpFactory, ILogger<VtexSimpleSessionService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Crear un cliente HTTP configurado para VTEX
    /// </summary>
    public HttpClient CreateVtexClient()
    {
        var handler = new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        var client = new HttpClient(handler);

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");

        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }

    /// <summary>
    /// Obtener orderForm usando GET (más simple y funciona según el test)
    /// </summary>
    public async Task<string?> GetOrCreateOrderFormAsync(string host, int salesChannel = 1, CancellationToken ct = default)
    {
        using var client = CreateVtexClient();

        // ✅ URL CORRECTA: /orderForm SIN la S
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm?sc={salesChannel}";

        _log.LogInformation("🔍 Getting orderForm: {Url}", url);

        try
        {
            // Usar GET como en el test exitoso
            using var response = await client.GetAsync(url, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _log.LogInformation("📊 OrderForm response: {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("❌ OrderForm failed: {Status} - {Body}", response.StatusCode,
                    responseBody.Length > 200 ? responseBody.Substring(0, 200) : responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("orderFormId", out var idElement))
            {
                var orderFormId = idElement.GetString();
                _log.LogInformation("✅ OrderForm obtenido: {OrderFormId}", orderFormId);
                return orderFormId;
            }

            _log.LogWarning("⚠️ OrderForm response sin orderFormId");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error obteniendo orderForm");
            return null;
        }
    }

    /// <summary>
    /// Agregar item al orderForm
    /// </summary>
    public async Task<bool> AddItemToOrderFormAsync(
        string host,
        string orderFormId,
        string skuId,
        string sellerId,
        int quantity = 1,
        CancellationToken ct = default)
    {
        using var client = CreateVtexClient();

        // ✅ URL CORRECTA: /orderForm SIN la S
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm/{orderFormId}/items";

        var payload = new
        {
            orderItems = new[]
            {
                new
                {
                    id = skuId,
                    quantity = quantity,
                    seller = sellerId
                }
            }
        };

        _log.LogInformation("➕ Agregando item {SkuId} al orderForm {OrderFormId}", skuId, orderFormId);

        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var response = await client.PostAsync(url, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _log.LogInformation("📊 Add item response: {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("❌ Add item failed: {Status} - {Body}", response.StatusCode,
                    responseBody.Length > 300 ? responseBody.Substring(0, 300) : responseBody);
                return false;
            }

            // Verificar que se agregó correctamente
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                _log.LogInformation("✅ Item agregado correctamente");
                return true;
            }

            _log.LogWarning("⚠️ Item no se agregó o respuesta sin items");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error agregando item");
            return false;
        }
    }

    /// <summary>
    /// Test simple de disponibilidad
    /// </summary>
    public async Task<SimpleAvailabilityResult> TestSimpleAvailabilityAsync(
        string host,
        string skuId,
        string sellerId,
        int salesChannel = 1,
        CancellationToken ct = default)
    {
        var result = new SimpleAvailabilityResult
        {
            Host = host,
            SkuId = skuId,
            SellerId = sellerId
        };

        try
        {
            // Paso 1: Obtener orderForm
            var orderFormId = await GetOrCreateOrderFormAsync(host, salesChannel, ct);
            if (string.IsNullOrEmpty(orderFormId))
            {
                result.Error = "No se pudo obtener orderForm";
                return result;
            }

            result.OrderFormId = orderFormId;

            // Paso 2: Agregar item
            var itemAdded = await AddItemToOrderFormAsync(host, orderFormId, skuId, sellerId, 1, ct);
            if (!itemAdded)
            {
                result.Error = "No se pudo agregar item";
                return result;
            }

            result.IsAvailable = true;
            result.Success = true;

            _log.LogInformation("✅ Test exitoso: {SkuId} disponible en {Host}", skuId, host);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _log.LogError(ex, "💥 Error en test simple para {SkuId}", skuId);
            return result;
        }
    }
}

public sealed class SimpleAvailabilityResult
{
    public string Host { get; set; } = default!;
    public string SkuId { get; set; } = default!;
    public string SellerId { get; set; } = default!;
    public string? OrderFormId { get; set; }
    public bool Success { get; set; }
    public bool IsAvailable { get; set; }
    public string? Error { get; set; }
}