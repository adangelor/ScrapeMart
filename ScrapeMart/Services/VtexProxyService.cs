// File: Services/VtexProxyService.cs
using System.Net;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// Servicio que usa Bright Data proxy para bypass de anti-bot
/// </summary>
public sealed class VtexProxyService
{
    private readonly IConfiguration _config;
    private readonly ILogger<VtexProxyService> _log;

    public VtexProxyService(IConfiguration config, ILogger<VtexProxyService> log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Crear HttpClient con proxy de Bright Data
    /// </summary>
    public HttpClient CreateProxyClient()
    {
        var proxyUrl = _config["Proxy:Url"];
        var proxyUsername = _config["Proxy:Username"];
        var proxyPassword = _config["Proxy:Password"];

        if (string.IsNullOrEmpty(proxyUrl) || string.IsNullOrEmpty(proxyUsername))
        {
            _log.LogWarning("⚠️ Proxy no configurado, usando conexión directa");
            return CreateDirectClient();
        }

        _log.LogInformation("🌐 Creando cliente con Bright Data proxy: {ProxyUrl}", proxyUrl);

        var proxy = new WebProxy(proxyUrl)
        {
            Credentials = new NetworkCredential(proxyUsername, proxyPassword)
        };

        var handler = new HttpClientHandler()
        {
            Proxy = proxy,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        var client = new HttpClient(handler);
        ConfigureClientHeaders(client);

        return client;
    }

    /// <summary>
    /// Cliente directo (sin proxy) como fallback
    /// </summary>
    public HttpClient CreateDirectClient()
    {
        var handler = new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        var client = new HttpClient(handler);
        ConfigureClientHeaders(client);

        return client;
    }

    /// <summary>
    /// Configurar headers para parecer un navegador real
    /// </summary>
    private void ConfigureClientHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        client.DefaultRequestHeaders.Add("Pragma", "no-cache");

        // Headers de Chrome real
        client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
        client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        client.DefaultRequestHeaders.Add("sec-fetch-dest", "empty");
        client.DefaultRequestHeaders.Add("sec-fetch-mode", "cors");
        client.DefaultRequestHeaders.Add("sec-fetch-site", "same-origin");

        client.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Test usando proxy vs directo
    /// </summary>
    public async Task<ProxyTestResult> CompareProxyVsDirectAsync(
        string host,
        int salesChannel = 1,
        CancellationToken ct = default)
    {
        var result = new ProxyTestResult { Host = host };

        // Test 1: Conexión directa
        _log.LogInformation("🔍 Testing conexión DIRECTA a {Host}", host);
        using var directClient = CreateDirectClient();
        result.DirectResult = await TestOrderFormAsync(directClient, host, salesChannel, "DIRECTA", ct);

        // Pequeña pausa entre tests
        await Task.Delay(1000, ct);

        // Test 2: Con proxy de Bright Data
        _log.LogInformation("🌐 Testing conexión con PROXY a {Host}", host);
        using var proxyClient = CreateProxyClient();
        result.ProxyResult = await TestOrderFormAsync(proxyClient, host, salesChannel, "PROXY", ct);

        // Comparar resultados
        if (result.ProxyResult.Success && !result.DirectResult.Success)
        {
            result.Recommendation = "✅ USAR PROXY - Bypass exitoso";
        }
        else if (!result.ProxyResult.Success && result.DirectResult.Success)
        {
            result.Recommendation = "✅ USAR DIRECTO - Proxy innecesario";
        }
        else if (result.ProxyResult.Success && result.DirectResult.Success)
        {
            result.Recommendation = "⚖️ AMBOS FUNCIONAN - Usar proxy para mayor anonimato";
        }
        else
        {
            result.Recommendation = "❌ AMBOS FALLAN - Problema más profundo (cookies, headers, etc)";
        }

        _log.LogInformation("📊 Resultado: {Recommendation}", result.Recommendation);
        return result;
    }

    /// <summary>
    /// Test individual de orderForm
    /// </summary>
    private async Task<SingleTestResult> TestOrderFormAsync(
        HttpClient client,
        string host,
        int salesChannel,
        string testName,
        CancellationToken ct)
    {
        var result = new SingleTestResult { TestName = testName };

        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm?sc={salesChannel}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", host + "/");
            request.Headers.Add("x-requested-with", "XMLHttpRequest");

            var startTime = DateTime.UtcNow;
            using var response = await client.SendAsync(request, ct);
            var responseTime = DateTime.UtcNow - startTime;

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            result.StatusCode = (int)response.StatusCode;
            result.ResponseTime = responseTime;
            result.ResponsePreview = responseBody.Length > 300 ? responseBody.Substring(0, 300) + "..." : responseBody;

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("orderFormId", out var idElement))
                {
                    result.OrderFormId = idElement.GetString();
                    result.Success = true;
                    _log.LogInformation("✅ {TestName}: OrderForm exitoso - {OrderFormId} ({ResponseTime}ms)",
                        testName, result.OrderFormId, responseTime.TotalMilliseconds);
                }
                else
                {
                    result.Error = "Respuesta sin orderFormId";
                }
            }
            else
            {
                result.Error = $"HTTP {response.StatusCode}";

                if (responseBody.Contains("CHK003"))
                {
                    result.Error = "BLOQUEADO (CHK003) - Acceso denegado";
                }
                else if (responseBody.Contains("CHK002"))
                {
                    result.Error = "VALIDACIÓN (CHK002) - Request inválido";
                }

                _log.LogWarning("❌ {TestName}: {Error} ({ResponseTime}ms)",
                    testName, result.Error, responseTime.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _log.LogError(ex, "💥 {TestName}: Exception", testName);
        }

        return result;
    }
}

public sealed class ProxyTestResult
{
    public string Host { get; set; } = default!;
    public SingleTestResult DirectResult { get; set; } = default!;
    public SingleTestResult ProxyResult { get; set; } = default!;
    public string Recommendation { get; set; } = default!;
}

public sealed class SingleTestResult
{
    public string TestName { get; set; } = default!;
    public int StatusCode { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public string? OrderFormId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ResponsePreview { get; set; }
}