// File: Services/VtexPublicClient.cs
using System.Text;
using System.Text.Json;


namespace ScrapeMart.Services;

public static class VtexPublicClientExtensions
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 1.1 Warmup - Homepage: Genera cookies de sesión básicas (de tu colección Postman)
    /// </summary>
    public static async Task<bool> WarmupHomepageAsync(this VtexPublicClient client, HttpClient http, string host, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{host.TrimEnd('/')}/", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 1.2 Crear OrderForm y Capturar ID (de tu colección Postman)
    /// </summary>
    public static async Task<OrderFormResult> CreateOrderFormAsync(this VtexPublicClient client, HttpClient http, string host, CancellationToken ct = default)
    {
        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Accept", "application/json");

            using var response = await http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("orderFormId", out var orderFormIdProperty))
                {
                    var orderFormId = orderFormIdProperty.GetString();
                    return new OrderFormResult(true, orderFormId, json);
                }
            }

            return new OrderFormResult(false, null, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return new OrderFormResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// 1.3 Buscar Producto y Capturar SKU/Seller por EAN específico (de tu colección Postman)
    /// </summary>
    public static async Task<ProductSearchResult> SearchProductByEanAsync(
        this VtexPublicClient client,
        HttpClient http,
        string host,
        string ean,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"{host.TrimEnd('/')}/api/catalog_system/pub/products/search?ft={Uri.EscapeDataString(ean)}&_from=0&_to=0";

            using var response = await http.GetAsync(url, ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var product = doc.RootElement[0];

                    if (product.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                    {
                        var item = items[0];
                        var itemId = item.TryGetProperty("itemId", out var itemIdProp) ? itemIdProp.GetString() : null;

                        if (item.TryGetProperty("sellers", out var sellers) && sellers.GetArrayLength() > 0)
                        {
                            var seller = sellers[0];
                            var sellerId = seller.TryGetProperty("sellerId", out var sellerIdProp) ? sellerIdProp.GetString() : null;
                            var productName = product.TryGetProperty("productName", out var nameProp) ? nameProp.GetString() : "";

                            decimal? price = null;
                            bool available = false;

                            if (seller.TryGetProperty("commertialOffer", out var offer))
                            {
                                if (offer.TryGetProperty("Price", out var priceProp))
                                    price = priceProp.GetDecimal();

                                if (offer.TryGetProperty("IsAvailable", out var availProp))
                                    available = availProp.GetBoolean();
                            }

                            return new ProductSearchResult(
                                true,
                                itemId!,
                                sellerId!,
                                productName!,
                                price,
                                available,
                                json
                            );
                        }
                    }
                }
            }

            // No encontrado
            return new ProductSearchResult(false, "", "", "", null, false, "{}");
        }
        catch (Exception ex)
        {
            return new ProductSearchResult(false, "", "", "Error: " + ex.Message, null, false, "{}");
        }
    }

    /// <summary>
    /// 2.1 Simulación Básica con coordenadas específicas de sucursal (de tu colección Postman)
    /// CORREGIDO: Incluye postalCode y maneja múltiples intentos
    /// </summary>
    public static async Task<SimulationResult> SimulateAtStoreLocationAsync(
        this VtexPublicClient client,
        HttpClient http,
        string host,
        string skuId,
        string sellerId,
        double longitude,
        double latitude,
        string? postalCode = null,
        string? city = null,    
        string? province = null,
        int salesChannel = 1,
        int quantity = 1,
        CancellationToken ct = default)
    {
        // Intentar múltiples configuraciones como en tu colección Postman
        var payloadConfigurations = new List<object>
{
    // Configuración 1
    new
    {
        items = new[] { new { id = skuId, quantity = quantity, seller = sellerId } },
        country = "AR",
        postalCode = postalCode ?? "1425",
        shippingData = new
        {
            address = new
            {
                addressType = "residential",
                country = "AR",
                geoCoordinates = new[] { longitude, latitude },
                city = city ?? "Buenos Aires",
                state = province ?? "CABA"
            }
        }
    },
    // Configuración 2
    new
    {
        items = new[] { new { id = skuId, quantity = quantity, seller = sellerId } },
        country = "AR",
        shippingData = new
        {
            address = new
            {
                addressType = "residential",
                country = "AR",
                geoCoordinates = new[] { longitude, latitude },
                city = city ?? "Buenos Aires",
                state = province ?? "CABA"
            }
        }
    },
    // Configuración 3
    new
    {
        items = new[] { new { id = skuId, quantity = quantity, seller = sellerId } },
        country = "AR",
        postalCode = postalCode ?? "1425",
        shippingData = new
        {
            address = new
            {
                addressType = "pickup",
                country = "AR",
                geoCoordinates = new[] { longitude, latitude },
                city = city ?? "Buenos Aires",
                state = province ?? "CABA"
            }
        }
    }
};
        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";

            // Probar cada configuración hasta que una funcione
            foreach (var payload in payloadConfigurations)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json")
                };

                request.Headers.Add("Accept", "application/json");
                request.Headers.Add("Referer", $"{host}/");

                using var response = await http.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    var pickupPoints = ExtractPickupPointsFromSimulation(json);
                    var availability = ExtractAvailabilityFromSimulation(json);
                    return new SimulationResult(true, json, pickupPoints, availability);
                }

                // Si es 500, intentar siguiente configuración
                if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                {
                    continue; // Intentar siguiente payload
                }

                // Si es otro error (400, 401, etc.), retornar inmediatamente
                return new SimulationResult(false, $"HTTP {(int)response.StatusCode}: {json}", new List<PickupPointInfo>(), null);
            }

            // Si todas las configuraciones fallaron con 500, el producto definitivamente no está disponible
            return new SimulationResult(false, "Producto no disponible en esta ubicación (todas las configuraciones fallaron con 500)",
                new List<PickupPointInfo>(),
                new ProductAvailability { Available = false, Quantity = 0, Price = 0, ListPrice = 0 });
        }
        catch (Exception ex)
        {
            return new SimulationResult(false, ex.Message, new List<PickupPointInfo>(), null);
        }
    }

    /// <summary>
    /// Extrae información de disponibilidad de la respuesta de simulación
    /// </summary>
    private static ProductAvailability? ExtractAvailabilityFromSimulation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            {
                var item = items[0];

                return new ProductAvailability
                {
                    Available = item.TryGetProperty("availability", out var avail) && avail.GetString() == "available",
                    Quantity = item.TryGetProperty("quantity", out var qty) ? qty.GetInt32() : 0,
                    Price = item.TryGetProperty("sellingPrice", out var price) ? price.GetDecimal() / 100 : 0,
                    ListPrice = item.TryGetProperty("listPrice", out var listPrice) ? listPrice.GetDecimal() / 100 : 0
                };
            }
        }
        catch
        {
            // En caso de error
        }

        return null;
    }

    /// <summary>
    /// Extrae pickup points de la respuesta de simulación
    /// </summary>
    private static List<PickupPointInfo> ExtractPickupPointsFromSimulation(string json)
    {
        var pickupPoints = new List<PickupPointInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("shippingData", out var shippingData) &&
                shippingData.TryGetProperty("logisticsInfo", out var logisticsInfo) &&
                logisticsInfo.ValueKind == JsonValueKind.Array && logisticsInfo.GetArrayLength() > 0)
            {
                var logistics = logisticsInfo[0];

                if (logistics.TryGetProperty("slas", out var slas) && slas.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sla in slas.EnumerateArray())
                    {
                        if (sla.TryGetProperty("deliveryChannel", out var deliveryChannel) &&
                            deliveryChannel.GetString() == "pickup-in-point")
                        {
                            var pickupPoint = new PickupPointInfo
                            {
                                Id = sla.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                                Name = sla.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                                ShippingPrice = sla.TryGetProperty("price", out var priceProp) ? priceProp.GetDecimal() / 100 : 0,
                                ShippingEstimate = sla.TryGetProperty("shippingEstimate", out var timeProp) ? timeProp.GetString() ?? "" : "",
                                Available = sla.TryGetProperty("available", out var availProp) ? availProp.GetBoolean() : false
                            };

                            if (sla.TryGetProperty("pickupStoreInfo", out var pickupStoreInfo))
                            {
                                pickupPoint.FriendlyName = pickupStoreInfo.TryGetProperty("friendlyName", out var friendlyProp) ?
                                                         friendlyProp.GetString() ?? "" : "";
                            }

                            pickupPoints.Add(pickupPoint);
                        }
                    }
                }
            }
        }
        catch
        {
            // En caso de error de parsing, devolver lista vacía
        }

        return pickupPoints;
    }


}

// DTOs para las extensiones
public sealed record OrderFormResult(bool Success, string? OrderFormId, string RawResponse);

public sealed record ProductSearchResult(
    bool Success,
    string SkuId,
    string SellerId,
    string ProductName,
    decimal? Price,
    bool Available,
    string RawResponse
);

public sealed record SimulationResult(
    bool Success,
    string RawResponse,
    List<PickupPointInfo> PickupPoints,
    ProductAvailability? Availability
);

public sealed class PickupPointInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public decimal ShippingPrice { get; set; }
    public string ShippingEstimate { get; set; } = "";
    public bool Available { get; set; }
}

public sealed class ProductAvailability
{
    public bool Available { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal ListPrice { get; set; }
}
