// 🚨 SI NADA FUNCIONA EN POSTMAN, HAY QUE EMPEZAR DESDE CERO

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Servicio de debugging básico para verificar qué carajo está pasando
/// </summary>
public sealed class BasicDebuggingService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BasicDebuggingService> _log;

    public BasicDebuggingService(IHttpClientFactory httpFactory, ILogger<BasicDebuggingService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Test básico: ¿Al menos la homepage responde?
    /// </summary>
    public async Task<DiagnosticResult> DiagnoseBasicConnectivityAsync(string host, CancellationToken ct = default)
    {
        var result = new DiagnosticResult { Host = host };

        _log.LogInformation("🔍 DIAGNOSTIC BÁSICO para {Host}", host);

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        // TEST 1: ¿La homepage responde?
        try
        {
            _log.LogInformation("TEST 1: Homepage básica");
            using var response = await http.GetAsync(host, ct);

            result.HomepageStatus = (int)response.StatusCode;
            result.HomepageSuccess = response.IsSuccessStatusCode;
            result.ResponseHeaders = response.Headers.ToString();

            _log.LogInformation("Homepage: {Status} - {Success}", response.StatusCode, response.IsSuccessStatusCode);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                result.HomepageContentLength = content.Length;
                result.ContainsVtexIndicators = content.Contains("vtex") || content.Contains("VTEX");

                _log.LogInformation("Content length: {Length}, Contains VTEX: {ContainsVtex}",
                    content.Length, result.ContainsVtexIndicators);
            }
        }
        catch (Exception ex)
        {
            result.HomepageError = ex.Message;
            _log.LogError(ex, "❌ Falló homepage");
        }

        // TEST 2: ¿El dominio es correcto?
        try
        {
            var uri = new Uri(host);
            result.HostInfo = $"Scheme: {uri.Scheme}, Host: {uri.Host}, Port: {uri.Port}";
            _log.LogInformation("Host info: {Info}", result.HostInfo);
        }
        catch (Exception ex)
        {
            result.HostInfo = $"Invalid URL: {ex.Message}";
            _log.LogError("❌ URL inválida: {Error}", ex.Message);
        }

        // TEST 3: ¿DNS resuelve?
        try
        {
            var uri = new Uri(host);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
            result.DnsResolution = string.Join(", ", addresses.Select(a => a.ToString()));
            _log.LogInformation("DNS resolution: {Addresses}", result.DnsResolution);
        }
        catch (Exception ex)
        {
            result.DnsResolution = $"DNS Error: {ex.Message}";
            _log.LogError("❌ DNS falló: {Error}", ex.Message);
        }

        // TEST 4: ¿Hay algún endpoint VTEX básico que responda?
        var vtexEndpoints = new[]
        {
            "/api/catalog_system/pub/category/tree/1",
            "/api/checkout/pub/orderForm",
            "/no-cache/sitemap.xml",
            "/robots.txt"
        };

        result.VtexEndpointResults = new Dictionary<string, string>();

        foreach (var endpoint in vtexEndpoints)
        {
            try
            {
                var testUrl = $"{host.TrimEnd('/')}{endpoint}";
                _log.LogInformation("Testing endpoint: {Endpoint}", testUrl);

                using var testResponse = await http.GetAsync(testUrl, ct);
                var status = $"{(int)testResponse.StatusCode} {testResponse.StatusCode}";

                result.VtexEndpointResults[endpoint] = status;
                _log.LogInformation("Endpoint {Endpoint}: {Status}", endpoint, status);
            }
            catch (Exception ex)
            {
                result.VtexEndpointResults[endpoint] = $"ERROR: {ex.Message}";
                _log.LogError("Error en {Endpoint}: {Error}", endpoint, ex.Message);
            }
        }

        return result;
    }

    /// <summary>
    /// Test de conectividad con diferentes user agents y headers
    /// </summary>
    public async Task<List<ConnectivityTest>> TestDifferentConfigurationsAsync(string host, CancellationToken ct = default)
    {
        var results = new List<ConnectivityTest>();

        // Configuraciones diferentes para probar
        var configurations = new[]
        {
            new TestConfig("Sin headers", new Dictionary<string, string>()),

            new TestConfig("Browser básico", new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
            }),

            new TestConfig("Full browser", new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
                ["Accept-Language"] = "es-AR,es;q=0.9,en;q=0.8",
                ["Accept-Encoding"] = "gzip, deflate, br",
                ["Connection"] = "keep-alive",
                ["Upgrade-Insecure-Requests"] = "1"
            }),

            new TestConfig("Mobile", new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1"
            })
        };

        foreach (var config in configurations)
        {
            var test = new ConnectivityTest { ConfigName = config.Name };

            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(15);

                foreach (var header in config.Headers)
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }

                _log.LogInformation("Testing config: {Config}", config.Name);

                using var response = await http.GetAsync(host, ct);

                test.StatusCode = (int)response.StatusCode;
                test.Success = response.IsSuccessStatusCode;
                test.ResponseTime = DateTime.UtcNow; // Aprox

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ct);
                    test.ContentLength = content.Length;
                    test.ContainsVtex = content.ToLower().Contains("vtex");
                }

                _log.LogInformation("Config {Config}: {Status} - Success: {Success}",
                    config.Name, response.StatusCode, response.IsSuccessStatusCode);
            }
            catch (Exception ex)
            {
                test.Error = ex.Message;
                _log.LogError("Error con config {Config}: {Error}", config.Name, ex.Message);
            }

            results.Add(test);
        }

        return results;
    }
}

// DTOs para diagnosis
public sealed class DiagnosticResult
{
    public string Host { get; set; } = "";
    public int? HomepageStatus { get; set; }
    public bool HomepageSuccess { get; set; }
    public string? HomepageError { get; set; }
    public int HomepageContentLength { get; set; }
    public bool ContainsVtexIndicators { get; set; }
    public string? ResponseHeaders { get; set; }
    public string? HostInfo { get; set; }
    public string? DnsResolution { get; set; }
    public Dictionary<string, string> VtexEndpointResults { get; set; } = new();
}

public sealed class ConnectivityTest
{
    public string ConfigName { get; set; } = "";
    public int? StatusCode { get; set; }
    public bool Success { get; set; }
    public DateTime ResponseTime { get; set; }
    public int ContentLength { get; set; }
    public bool ContainsVtex { get; set; }
    public string? Error { get; set; }
}

public sealed record TestConfig(string Name, Dictionary<string, string> Headers);
