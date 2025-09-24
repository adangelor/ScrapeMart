using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// Servicio para manejar sesiones y cookies de VTEX como un navegador real
/// </summary>
public sealed class VtexSessionService
{
    private readonly ILogger<VtexSessionService> _log;
    private readonly CookieContainer _cookieContainer;
    private readonly HttpClient _httpClient;

    public VtexSessionService(IHttpClientFactory httpFactory, ILogger<VtexSessionService> log)
    {
        _log = log;
        _cookieContainer = new CookieContainer();

        var handler = new HttpClientHandler()
        {
            CookieContainer = _cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli // 🔧 ESTO ARREGLA LOS SÍMBOLOS RAROS
        };

        _httpClient = new HttpClient(handler);
        SetupHttpClient();
    }

    private void SetupHttpClient()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html", 0.9));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-AR,es;q=0.9,en;q=0.8");
        // 🔧 QUITAR ESTA LÍNEA - el AutomaticDecompression ya lo maneja
        // _httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");

        _httpClient.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
        _httpClient.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        _httpClient.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        _httpClient.DefaultRequestHeaders.Add("sec-fetch-dest", "empty");
        _httpClient.DefaultRequestHeaders.Add("sec-fetch-mode", "cors");
        _httpClient.DefaultRequestHeaders.Add("sec-fetch-site", "same-origin");

        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Inicializa una sesión completa para un host de VTEX (como usuario anónimo)
    /// </summary>
    public async Task<SessionInfo> InitializeSessionAsync(string host, int salesChannel = 1, CancellationToken ct = default)
    {
        _log.LogInformation("🔐 Inicializando sesión ANÓNIMA para {Host} (SC: {SC})", host, salesChannel);

        var session = new SessionInfo { Host = host, SalesChannel = salesChannel };

        try
        {
            // 1. Visitar página principal para obtener cookies básicas
            await VisitHomePage(host, ct);

            // 2. Llamar a segment para configurar región/canal  
            await InitializeSegment(host, salesChannel, ct);

            // 3. Inicializar region por defecto (Argentina)
            await InitializeRegion(host, salesChannel, ct);

            // 4. Intentar crear un orderForm para validar la sesión
            session.OrderFormId = await CreateInitialOrderForm(host, salesChannel, ct);

            // 5. Capturar cookies importantes
            session.Cookies = ExtractImportantCookies(host);

            session.IsValid = !string.IsNullOrEmpty(session.OrderFormId);

            _log.LogInformation("✅ Sesión anónima {Status} para {Host}. OrderForm: {OrderFormId}",
                session.IsValid ? "VÁLIDA" : "INVÁLIDA", host, session.OrderFormId);

            return session;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Error inicializando sesión anónima para {Host}", host);
            session.IsValid = false;
            session.Error = ex.Message;
            return session;
        }
    }

    /// <summary>
    /// PASO 1: Visitar página principal
    /// </summary>
    private async Task VisitHomePage(string host, CancellationToken ct)
    {
        _log.LogDebug("🏠 Visitando página principal: {Host}", host);

        using var request = new HttpRequestMessage(HttpMethod.Get, host);
        request.Headers.Add("sec-fetch-dest", "document");
        request.Headers.Add("sec-fetch-mode", "navigate");
        request.Headers.Add("sec-fetch-site", "none");
        request.Headers.Add("sec-fetch-user", "?1");

        using var response = await _httpClient.SendAsync(request, ct);

        _log.LogDebug("🏠 Home page: {Status}", response.StatusCode);
    }

    /// <summary>
    /// PASO 2: Inicializar segment
    /// </summary>
    private async Task InitializeSegment(string host, int salesChannel, CancellationToken ct)
    {
        var segmentUrl = $"{host.TrimEnd('/')}/_v/segment/graphql/v1";

        _log.LogDebug("🎯 Inicializando segment: {Url}", segmentUrl);

        // Query GraphQL para inicializar segment
        var segmentQuery = new
        {
            query = @"
                query {
                    publicSettings {
                        defaultChannel
                        supportedChannels
                    }
                }",
            variables = new { }
        };

        var json = JsonSerializer.Serialize(segmentQuery);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, segmentUrl) { Content = content };

        // Headers específicos para GraphQL
        request.Headers.Add("x-vtex-use-https", "true");
        request.Headers.Referrer = new Uri(host);

        using var response = await _httpClient.SendAsync(request, ct);

        _log.LogDebug("🎯 Segment: {Status}", response.StatusCode);
    }

    /// <summary>
    /// PASO 3: Inicializar región por defecto
    /// </summary>
    private async Task InitializeRegion(string host, int salesChannel, CancellationToken ct)
    {
        // Configurar región por defecto (Argentina, Buenos Aires)
        var regionUrl = $"{host.TrimEnd('/')}/api/checkout/pub/regions?country=AR&postalCode=1000&sc={salesChannel}";

        _log.LogDebug("🌍 Inicializando región: {Url}", regionUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, regionUrl);
        request.Headers.Referrer = new Uri(host);
        request.Headers.Add("x-requested-with", "XMLHttpRequest");

        using var response = await _httpClient.SendAsync(request, ct);

        _log.LogDebug("🌍 Region: {Status}", response.StatusCode);
    }

    /// <summary>
    /// PASO 4: Crear orderForm inicial
    /// </summary>
    private async Task<string?> CreateInitialOrderForm(string host, int salesChannel, CancellationToken ct)
    {
        // ✅ URL CORREGIDA: /orderForm SIN la S
        var orderFormUrl = $"{host.TrimEnd('/')}/api/checkout/pub/orderForm?sc={salesChannel}";

        _log.LogDebug("📝 Creando orderForm: {Url}", orderFormUrl);

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, orderFormUrl) { Content = content };

        request.Headers.Referrer = new Uri(host);
        request.Headers.Add("x-requested-with", "XMLHttpRequest");
        // 🔧 QUITAR ESTA LÍNEA - ya se maneja automáticamente
        // request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");

        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("⚠️ OrderForm creation failed: {Status} - {Body}", response.StatusCode, responseBody);
            return null;
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("orderFormId", out var idElement))
        {
            var orderFormId = idElement.GetString();
            _log.LogDebug("📝 OrderForm creado: {OrderFormId}", orderFormId);
            return orderFormId;
        }

        return null;
    }

    /// <summary>
    /// PASO 4: Extraer cookies importantes
    /// </summary>
    private Dictionary<string, string> ExtractImportantCookies(string host)
    {
        var cookies = new Dictionary<string, string>();
        var uri = new Uri(host);

        foreach (Cookie cookie in _cookieContainer.GetCookies(uri))
        {
            // Solo guardamos las cookies importantes
            if (IsImportantCookie(cookie.Name))
            {
                cookies[cookie.Name] = cookie.Value;
            }
        }

        _log.LogDebug("🍪 Cookies capturadas: {Count}", cookies.Count);
        return cookies;
    }

    private static bool IsImportantCookie(string cookieName)
    {
        var importantCookies = new[]
        {
            "vtex_session",
            "vtex_segment",
            "VtexIdclientAutCookie",
            "checkout.vtex.com",
            "vtex-search-session",
            "vtex-search-anonymous",
            "VtexWorkspace"
        };

        return importantCookies.Any(important =>
            cookieName.StartsWith(important, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Usar la sesión para hacer requests autenticados
    /// </summary>
    public async Task<HttpResponseMessage> SendAuthenticatedRequestAsync(
        HttpMethod method,
        string url,
        HttpContent? content = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        // Agregar headers de sesión autenticada
        var uri = new Uri(url);
        request.Headers.Referrer = new Uri($"{uri.Scheme}://{uri.Host}/");
        request.Headers.Add("x-requested-with", "XMLHttpRequest");

        return await _httpClient.SendAsync(request, ct);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

/// <summary>
/// Información de sesión de VTEX
/// </summary>
public sealed class SessionInfo
{
    public string Host { get; set; } = default!;
    public int SalesChannel { get; set; }
    public string? OrderFormId { get; set; }
    public Dictionary<string, string> Cookies { get; set; } = new();
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsExpired => DateTime.UtcNow - CreatedAt > TimeSpan.FromHours(1);
}