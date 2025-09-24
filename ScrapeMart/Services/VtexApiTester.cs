// File: Services/VtexApiTester.cs
namespace ScrapeMart.Services;

/// <summary>
/// Servicio para testear las APIs de VTEX y descubrir cómo funcionan realmente
/// </summary>
public sealed class VtexApiTester
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<VtexApiTester> _log;

    public VtexApiTester(IHttpClientFactory httpFactory, ILogger<VtexApiTester> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Testear diferentes endpoints de orderForm para ver cuál funciona
    /// </summary>
    public async Task<ApiTestResults> TestOrderFormEndpointsAsync(string host, int salesChannel = 1, CancellationToken ct = default)
    {
        _log.LogInformation("🧪 Testing OrderForm endpoints para {Host}", host);

        var results = new ApiTestResults { Host = host };
        var http = _httpFactory.CreateClient("vtexSession");

        // Setup básico del cliente
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        http.DefaultRequestHeaders.Add("Referer", host);

        // Test 1: POST /api/checkout/pub/orderForms (lo que estamos probando)
        await TestEndpoint(http, "POST", $"{host}/api/checkout/pub/orderForm?sc={salesChannel}", "{}", results, "CreateOrderForm_POST");

        // Test 2: GET /api/checkout/pub/orderForms (tal vez necesitamos GET?)
        await TestEndpoint(http, "GET", $"{host}/api/checkout/pub/orderForm?sc={salesChannel}", null, results, "CreateOrderForm_GET");

        // Test 3: POST sin sales channel
        await TestEndpoint(http, "POST", $"{host}/api/checkout/pub/orderForm", "{}", results, "CreateOrderForm_POST_NoSC");

        // Test 4: Simulation directa (más simple)
        var simPayload = """
        {
            "items": [],
            "postalCode": "1000",
            "country": "AR"
        }
        """;
        await TestEndpoint(http, "POST", $"{host}/api/checkout/pub/orderForm/simulation?sc={salesChannel}", simPayload, results, "Simulation");

        // Test 5: Regions (para ver si el host funciona básicamente)
        await TestEndpoint(http, "GET", $"{host}/api/checkout/pub/regions?country=AR&postalCode=1000&sc={salesChannel}", null, results, "Regions");

        // Test 6: Pickup points (para verificar que el host es correcto)
        await TestEndpoint(http, "GET", $"{host}/api/checkout/pub/pickup-points?geoCoordinates=-58.3816;-34.6037&sc={salesChannel}", null, results, "PickupPoints");

        return results;
    }

    private async Task TestEndpoint(HttpClient http, string method, string url, string? payload, ApiTestResults results, string testName)
    {
        try
        {
            _log.LogInformation("🔍 Testing {TestName}: {Method} {Url}", testName, method, url);

            HttpResponseMessage response;
            if (method == "POST" && payload != null)
            {
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                response = await http.PostAsync(url, content);
            }
            else
            {
                response = await http.GetAsync(url);
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var testResult = new ApiTestResult
            {
                TestName = testName,
                Method = method,
                Url = url,
                StatusCode = (int)response.StatusCode,
                StatusText = response.StatusCode.ToString(),
                ResponseBody = responseBody.Length > 1000 ? responseBody.Substring(0, 1000) + "..." : responseBody,
                IsSuccess = response.IsSuccessStatusCode,
                Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value))
            };

            results.Tests.Add(testResult);

            _log.LogInformation("📊 {TestName}: {Status} ({StatusCode})", testName, response.StatusCode, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("❌ {TestName} failed: {Body}", testName, responseBody.Length > 200 ? responseBody.Substring(0, 200) : responseBody);
            }
            else
            {
                _log.LogInformation("✅ {TestName} success!", testName);
            }

            response.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error testing {TestName}", testName);
            results.Tests.Add(new ApiTestResult
            {
                TestName = testName,
                Method = method,
                Url = url,
                StatusCode = 0,
                StatusText = "Exception",
                ResponseBody = ex.Message,
                IsSuccess = false
            });
        }
    }
}

public sealed class ApiTestResults
{
    public string Host { get; set; } = default!;
    public List<ApiTestResult> Tests { get; set; } = new();
    public DateTime TestedAt { get; set; } = DateTime.UtcNow;

    public ApiTestResult? GetSuccessfulTest() => Tests.FirstOrDefault(t => t.IsSuccess);
    public List<ApiTestResult> GetFailedTests() => Tests.Where(t => !t.IsSuccess).ToList();
}

public sealed class ApiTestResult
{
    public string TestName { get; set; } = default!;
    public string Method { get; set; } = default!;
    public string Url { get; set; } = default!;
    public int StatusCode { get; set; }
    public string StatusText { get; set; } = default!;
    public string ResponseBody { get; set; } = default!;
    public bool IsSuccess { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}