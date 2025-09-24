using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

/// <summary>
/// Flujo que SÍ funciona basado en los endpoints que dan 200 OK
/// EVITA completamente los pickup points problemáticos
/// </summary>
public sealed class WorkingVtexFlowService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WorkingVtexFlowService> _log;
    private readonly string _connectionString;

    public WorkingVtexFlowService(IHttpClientFactory httpFactory, ILogger<WorkingVtexFlowService> log, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _log = log;
        _connectionString = config.GetConnectionString("Default")!;
    }

    /// <summary>
    /// Flujo que REALMENTE funciona - evita pickup points completamente
    /// </summary>
    public async Task<WorkingFlowResult> RunWorkingFlowAsync(string host, CancellationToken ct = default)
    {
        var result = new WorkingFlowResult { Host = host };

        _log.LogInformation("Ejecutando flujo que SÍ funciona para {Host}", host);

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        try
        {
            // PASO 1: Crear OrderForm (esto SÍ funciona)
            _log.LogInformation("PASO 1: Creando OrderForm");
            var orderFormId = await CreateOrderFormAsync(http, host, ct);
            if (string.IsNullOrEmpty(orderFormId))
            {
                result.ErrorMessage = "No se pudo crear OrderForm";
                return result;
            }
            result.OrderFormId = orderFormId;
            _log.LogInformation("OrderForm creado: {OrderFormId}", orderFormId);

            // PASO 2: Buscar productos que existan (esto SÍ funciona)
            _log.LogInformation("PASO 2: Buscando productos reales");
            var products = await FindRealProductsAsync(http, host, ct);
            if (products.Count == 0)
            {
                result.ErrorMessage = "No se encontraron productos";
                return result;
            }
            result.ProductsFound = products.Count;
            _log.LogInformation("Productos encontrados: {Count}", products.Count);

            // PASO 3: Probar simulaciones DIRECTAS sin pickup points problemáticos
            _log.LogInformation("PASO 3: Simulaciones directas");
            var testProduct = products.First();

            // Códigos postales argentinos que SÍ deberían funcionar
            var postalCodes = new[]
            {
                ("1425", "CABA", "Buenos Aires"),
                ("1406", "CABA", "Buenos Aires"),
                ("1640", "Buenos Aires", "Buenos Aires"),
                ("1900", "Buenos Aires", "Buenos Aires"),
                ("5000", "Córdoba", "Córdoba")
            };

            foreach (var (postal, city, province) in postalCodes)
            {
                var simResult = await TestDirectSimulationAsync(http, host, testProduct, postal, city, province, ct);
                result.SimulationResults.Add(simResult);

                if (simResult.Success)
                {
                    _log.LogInformation("Simulación exitosa en {City} ({Postal}): {Analysis}",
                        city, postal, simResult.Analysis);

                    // Si encontramos pickup points en la respuesta, guardarlos
                    if (simResult.PickupPointsFound > 0)
                    {
                        await SaveDiscoveredPickupPointsAsync(host, postal, city, province, simResult.RawResponse, ct);
                    }
                }
            }

            result.Success = result.SimulationResults.Any(s => s.Success);

            _log.LogInformation("Flujo completado. Simulaciones exitosas: {Count}",
                result.SimulationResults.Count(s => s.Success));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error en flujo working");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<string?> CreateOrderFormAsync(HttpClient http, string host, CancellationToken ct)
    {
        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("orderFormId", out var orderFormId))
                {
                    return orderFormId.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error creando OrderForm");
        }
        return null;
    }

    private async Task<List<WorkingProduct>> FindRealProductsAsync(HttpClient http, string host, CancellationToken ct)
    {
        var products = new List<WorkingProduct>();

        // Términos que SÍ devuelven productos en las cadenas argentinas
        var searchTerms = new[] { "coca", "pepsi", "leche", "yogurt", "pan" };

        foreach (var term in searchTerms)
        {
            try
            {
                var searchUrl = $"{host.TrimEnd('/')}/api/catalog_system/pub/products/search?ft={Uri.EscapeDataString(term)}&_from=0&_to=5";
                var response = await http.GetAsync(searchUrl, ct);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrEmpty(json) && json != "[]")
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var productElement in doc.RootElement.EnumerateArray())
                                {
                                    var product = ParseProduct(productElement);
                                    if (product != null && !products.Any(p => p.ItemId == product.ItemId))
                                    {
                                        products.Add(product);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning("Error parseando productos para {Term}: {Error}", term, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Error buscando {Term}: {Error}", term, ex.Message);
            }
        }

        return products.Take(5).ToList(); // Limitar para testing
    }

    private async Task<SimulationResult> TestDirectSimulationAsync(
        HttpClient http, string host, WorkingProduct product,
        string postalCode, string city, string province, CancellationToken ct)
    {
        var result = new SimulationResult
        {
            PostalCode = postalCode,
            City = city,
            Province = province
        };

        try
        {
            // Payload básico SIN pickup points problemáticos
            var payload = new
            {
                items = new[]
                {
                    new
                    {
                        id = product.ItemId,
                        quantity = 1,
                        seller = product.SellerId
                    }
                },
                country = "AR",
                postalCode = postalCode,
                shippingData = new
                {
                    address = new
                    {
                        addressType = "residential",
                        country = "AR",
                        city = city,
                        state = province
                    }
                }
            };

            var simulationUrl = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc=1";

            using var request = new HttpRequestMessage(HttpMethod.Post, simulationUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Referer", $"{host}/");

            using var response = await http.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            result.StatusCode = (int)response.StatusCode;
            result.Success = response.IsSuccessStatusCode;
            result.RawResponse = responseBody;

            if (response.IsSuccessStatusCode)
            {
                result.Analysis = AnalyzeSimulationResponse(responseBody);
                result.PickupPointsFound = CountPickupPointsInResponse(responseBody);
            }
            else
            {
                result.ErrorMessage = responseBody.Length > 200 ? responseBody.Substring(0, 200) + "..." : responseBody;
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task SaveDiscoveredPickupPointsAsync(
        string host, string postalCode, string city, string province,
        string simulationResponse, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(simulationResponse);

            // Buscar pickup points en diferentes lugares de la respuesta
            var pickupPoints = new List<DiscoveredPickupPoint>();

            // En shippingData.logisticsInfo[].slas[]
            if (doc.RootElement.TryGetProperty("shippingData", out var shipping) &&
                shipping.TryGetProperty("logisticsInfo", out var logistics))
            {
                foreach (var logistic in logistics.EnumerateArray())
                {
                    if (logistic.TryGetProperty("slas", out var slas))
                    {
                        foreach (var sla in slas.EnumerateArray())
                        {
                            if (sla.TryGetProperty("deliveryChannel", out var channel) &&
                                channel.GetString() == "pickup-in-point")
                            {
                                var pickupPoint = new DiscoveredPickupPoint
                                {
                                    Id = sla.TryGetProperty("id", out var id) ? id.GetString() : null,
                                    Name = sla.TryGetProperty("name", out var name) ? name.GetString() : null,
                                    PostalCode = postalCode,
                                    City = city,
                                    Province = province
                                };

                                if (!string.IsNullOrEmpty(pickupPoint.Id))
                                {
                                    pickupPoints.Add(pickupPoint);
                                }
                            }
                        }
                    }
                }
            }

            // Guardar en base de datos para uso futuro
            foreach (var pp in pickupPoints)
            {
                await SavePickupPointToDbAsync(host, pp, ct);
            }

            _log.LogInformation("Guardados {Count} pickup points descobertos para {City}",
                pickupPoints.Count, city);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Error guardando pickup points descobertos: {Error}", ex.Message);
        }
    }

    private async Task SavePickupPointToDbAsync(string host, DiscoveredPickupPoint pickupPoint, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO DiscoveredPickupPoints (RetailerHost, PickupPointId, Name, PostalCode, City, Province, DiscoveredAt)
            VALUES (@host, @id, @name, @postal, @city, @province, GETUTCDATE())
            ON DUPLICATE KEY UPDATE DiscoveredAt = GETUTCDATE()";

        // Implementar inserción en base de datos...
        // Por ahora solo log
        _log.LogInformation("Pickup Point descoberto: {Id} - {Name} en {City}",
            pickupPoint.Id, pickupPoint.Name, pickupPoint.City);
    }

    private WorkingProduct? ParseProduct(JsonElement productElement)
    {
        try
        {
            var product = new WorkingProduct();

            if (productElement.TryGetProperty("productName", out var name))
                product.ProductName = name.GetString();

            if (productElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var item = items[0];

                if (item.TryGetProperty("itemId", out var itemId))
                    product.ItemId = itemId.GetString();

                if (item.TryGetProperty("sellers", out var sellers) && sellers.GetArrayLength() > 0)
                {
                    var seller = sellers[0];
                    if (seller.TryGetProperty("sellerId", out var sellerId))
                        product.SellerId = sellerId.GetString();
                }
            }

            return !string.IsNullOrEmpty(product.ItemId) && !string.IsNullOrEmpty(product.SellerId)
                ? product : null;
        }
        catch
        {
            return null;
        }
    }

    private string AnalyzeSimulationResponse(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var parts = new List<string>();

            if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var item = items[0];
                if (item.TryGetProperty("availability", out var avail))
                    parts.Add($"Disponible: {avail.GetString()}");

                if (item.TryGetProperty("sellingPrice", out var price))
                    parts.Add($"Precio: ${price.GetDecimal() / 100:F2}");
            }

            return string.Join(", ", parts);
        }
        catch
        {
            return "Error analizando";
        }
    }

    private int CountPickupPointsInResponse(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            int count = 0;

            if (doc.RootElement.TryGetProperty("shippingData", out var shipping) &&
                shipping.TryGetProperty("logisticsInfo", out var logistics))
            {
                foreach (var logistic in logistics.EnumerateArray())
                {
                    if (logistic.TryGetProperty("slas", out var slas))
                    {
                        count += slas.EnumerateArray()
                            .Count(sla => sla.TryGetProperty("deliveryChannel", out var dc) &&
                                         dc.GetString() == "pickup-in-point");
                    }
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }
}

// DTOs
public sealed class WorkingFlowResult
{
    public string Host { get; set; } = "";
    public bool Success { get; set; }
    public string? OrderFormId { get; set; }
    public int ProductsFound { get; set; }
    public List<SimulationResult> SimulationResults { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public sealed class WorkingProduct
{
    public string? ProductName { get; set; }
    public string? ItemId { get; set; }
    public string? SellerId { get; set; }
}

public sealed class SimulationResult
{
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
    public int? StatusCode { get; set; }
    public bool Success { get; set; }
    public string? Analysis { get; set; }
    public int PickupPointsFound { get; set; }
    public string? RawResponse { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class DiscoveredPickupPoint
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
}

