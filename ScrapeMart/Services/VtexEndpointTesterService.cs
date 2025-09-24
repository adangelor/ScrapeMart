using System.Text;
using System.Text.Json;

/// <summary>
/// Tester específico para endpoints VTEX - ahora que sabemos que la conectividad básica funciona
/// </summary>
public sealed class VtexEndpointTesterService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<VtexEndpointTesterService> _log;

    public VtexEndpointTesterService(IHttpClientFactory httpFactory, ILogger<VtexEndpointTesterService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Test sistemático de endpoints VTEX con diferentes configuraciones
    /// </summary>
    public async Task<List<VtexEndpointResult>> TestVtexEndpointsAsync(string host, CancellationToken ct = default)
    {
        var results = new List<VtexEndpointResult>();

        // Endpoints VTEX para probar en orden
        var endpoints = new[]
        {
            // Endpoints públicos básicos
            new EndpointTest("Category Tree", "GET", "/api/catalog_system/pub/category/tree/1", null),
            new EndpointTest("OrderForm Creation", "POST", "/api/checkout/pub/orderForm", "{}"),
            
            // Búsqueda de productos (con EANs comunes)
            new EndpointTest("Product Search - EAN genérico", "GET", "/api/catalog_system/pub/products/search?ft=7790895000016&_from=0&_to=0", null),
            new EndpointTest("Product Search - término genérico", "GET", "/api/catalog_system/pub/products/search?ft=coca&_from=0&_to=0", null),
            new EndpointTest("Product Search - término básico", "GET", "/api/catalog_system/pub/products/search?ft=a&_from=0&_to=10", null),
            
            // Pickup points
            new EndpointTest("Pickup Points - CABA", "GET", "/api/checkout/pub/pickup-points?postalCode=1425&countryCode=AR&sc=1", null),
            new EndpointTest("Pickup Points - Coordenadas CABA", "GET", "/api/checkout/pub/pickup-points?geoCoordinates=-58.3816,-34.6037&sc=1", null),
            
            // Regiones
            new EndpointTest("Regions - CABA", "GET", "/api/checkout/pub/regions?country=AR&postalCode=1425&sc=1", null),
            
            // Simulación básica (necesita SKU real)
            // new EndpointTest("Simulation", "POST", "/api/checkout/pub/orderForms/simulation?sc=1", simulationPayload)
        };

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        // Headers que funcionaron en el test anterior
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");

        foreach (var endpoint in endpoints)
        {
            var result = new VtexEndpointResult
            {
                Name = endpoint.Name,
                Method = endpoint.Method,
                Endpoint = endpoint.Path,
                FullUrl = $"{host.TrimEnd('/')}{endpoint.Path}"
            };

            try
            {
                _log.LogInformation("Testing: {Method} {Endpoint}", endpoint.Method, endpoint.Path);

                HttpResponseMessage response;

                if (endpoint.Method == "GET")
                {
                    response = await http.GetAsync(result.FullUrl, ct);
                }
                else if (endpoint.Method == "POST")
                {
                    var content = new StringContent(endpoint.Body ?? "{}", Encoding.UTF8, "application/json");
                    response = await http.PostAsync(result.FullUrl, content, ct);
                }
                else
                {
                    throw new NotSupportedException($"Method {endpoint.Method} not supported");
                }

                result.StatusCode = (int)response.StatusCode;
                result.Success = response.IsSuccessStatusCode;
                result.ResponseBody = await response.Content.ReadAsStringAsync(ct);
                result.ContentLength = result.ResponseBody.Length;

                // Análisis básico de la respuesta
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(result.ResponseBody);
                        result.ValidJson = true;
                        result.JsonStructure = AnalyzeJsonStructure(doc.RootElement);

                        // Extraer información útil específica por endpoint
                        result.ExtractedData = ExtractUsefulData(endpoint.Name, doc.RootElement);
                    }
                    catch
                    {
                        result.ValidJson = false;
                        result.JsonStructure = "Invalid JSON or HTML response";
                    }
                }

                _log.LogInformation("Result: {Status} - Length: {Length} - Valid JSON: {ValidJson}",
                    response.StatusCode, result.ContentLength, result.ValidJson);

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("Error response: {Body}",
                        result.ResponseBody.Length > 500 ? result.ResponseBody.Substring(0, 500) + "..." : result.ResponseBody);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _log.LogError(ex, "Error testing endpoint {Endpoint}", endpoint.Path);
            }

            results.Add(result);

            // Pequeña pausa entre requests
            await Task.Delay(1000, ct);
        }

        return results;
    }

    /// <summary>
    /// Test específico para encontrar un producto que funcione
    /// </summary>
    public async Task<ProductDiscoveryResult> FindWorkingProductAsync(string host, CancellationToken ct = default)
    {
        var result = new ProductDiscoveryResult { Host = host };

        // EANs comunes en Argentina para probar
        var testEans = new[]
        {
            "7790895000016", // Coca Cola común
            "7790895001037", // Coca Cola Zero
            "7622210951236", // Oreo común
            "7622300311636", // Oreo Double
            "7790742000106", // Leche La Serenísima
            "7790070011084", // Yogurt común
            "7798062883205", // Pan común
            "7790070022202"  // Manteca común
        };

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        foreach (var ean in testEans)
        {
            try
            {
                _log.LogInformation("Probando EAN: {EAN}", ean);

                var searchUrl = $"{host.TrimEnd('/')}/api/catalog_system/pub/products/search?ft={ean}&_from=0&_to=0";
                var response = await http.GetAsync(searchUrl, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                        {
                            var product = doc.RootElement[0];

                            // Extraer información del producto
                            var productInfo = new ProductInfo
                            {
                                EAN = ean,
                                ProductName = product.TryGetProperty("productName", out var name) ? name.GetString() : null,
                                ProductId = product.TryGetProperty("productId", out var id) ? id.GetString() : null
                            };

                            // Extraer SKU y seller
                            if (product.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                            {
                                var item = items[0];
                                productInfo.ItemId = item.TryGetProperty("itemId", out var itemId) ? itemId.GetString() : null;

                                if (item.TryGetProperty("sellers", out var sellers) && sellers.GetArrayLength() > 0)
                                {
                                    var seller = sellers[0];
                                    productInfo.SellerId = seller.TryGetProperty("sellerId", out var sellerId) ? sellerId.GetString() : null;
                                    productInfo.SellerName = seller.TryGetProperty("sellerName", out var sellerName) ? sellerName.GetString() : null;

                                    // Extraer precio si está disponible
                                    if (seller.TryGetProperty("commertialOffer", out var offer))
                                    {
                                        if (offer.TryGetProperty("Price", out var price))
                                            productInfo.Price = price.GetDecimal();
                                        if (offer.TryGetProperty("IsAvailable", out var available))
                                            productInfo.Available = available.GetBoolean();
                                    }
                                }
                            }

                            result.WorkingProducts.Add(productInfo);
                            result.Success = true;

                            _log.LogInformation("✅ Producto encontrado: {Name} - SKU: {SKU} - Seller: {Seller}",
                                productInfo.ProductName, productInfo.ItemId, productInfo.SellerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("Error parseando respuesta para EAN {EAN}: {Error}", ean, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Error buscando EAN {EAN}: {Error}", ean, ex.Message);
            }
        }

        return result;
    }

    private string AnalyzeJsonStructure(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Array => $"Array[{element.GetArrayLength()}]",
            JsonValueKind.Object => $"Object with {element.EnumerateObject().Count()} properties",
            JsonValueKind.String => "String",
            JsonValueKind.Number => "Number",
            JsonValueKind.True or JsonValueKind.False => "Boolean",
            JsonValueKind.Null => "null",
            _ => "Unknown"
        };
    }

    private Dictionary<string, object?> ExtractUsefulData(string endpointName, JsonElement element)
    {
        var data = new Dictionary<string, object?>();

        try
        {
            switch (endpointName)
            {
                case "Category Tree":
                    if (element.ValueKind == JsonValueKind.Array)
                        data["CategoryCount"] = element.GetArrayLength();
                    break;

                case "OrderForm Creation":
                    if (element.TryGetProperty("orderFormId", out var orderFormId))
                        data["OrderFormId"] = orderFormId.GetString();
                    break;

                case var name when name.Contains("Product Search"):
                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        data["ProductCount"] = element.GetArrayLength();
                        if (element.GetArrayLength() > 0)
                        {
                            var firstProduct = element[0];
                            if (firstProduct.TryGetProperty("productName", out var productName))
                                data["FirstProductName"] = productName.GetString();
                        }
                    }
                    break;

                case var name when name.Contains("Pickup Points"):
                    if (element.ValueKind == JsonValueKind.Array)
                        data["PickupPointCount"] = element.GetArrayLength();
                    else if (element.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        data["PickupPointCount"] = items.GetArrayLength();
                    break;
            }
        }
        catch
        {
            // Ignorar errores de extracción
        }

        return data;
    }
}

// DTOs
public sealed class VtexEndpointResult
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string FullUrl { get; set; } = "";
    public int? StatusCode { get; set; }
    public bool Success { get; set; }
    public string? ResponseBody { get; set; }
    public int ContentLength { get; set; }
    public bool ValidJson { get; set; }
    public string? JsonStructure { get; set; }
    public Dictionary<string, object?> ExtractedData { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class ProductDiscoveryResult
{
    public string Host { get; set; } = "";
    public bool Success { get; set; }
    public List<ProductInfo> WorkingProducts { get; set; } = new();
}

public sealed class ProductInfo
{
    public string? EAN { get; set; }
    public string? ProductName { get; set; }
    public string? ProductId { get; set; }
    public string? ItemId { get; set; }
    public string? SellerId { get; set; }
    public string? SellerName { get; set; }
    public decimal? Price { get; set; }
    public bool? Available { get; set; }
}

public sealed record EndpointTest(string Name, string Method, string Path, string? Body);
