using System.Net;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

public interface IVtexCookieManager
{
    void SetCookiesForHost(string host, string cookieString);
    CookieContainer GetCookieContainer(string host);
    void UpdateSegmentCookie(string host, int salesChannel, string? regionId = null);
    Task WarmupCookiesAsync(HttpClient httpClient, string host, CancellationToken ct = default);
}

public sealed class VtexCookieManager : IVtexCookieManager
{
    private readonly Dictionary<string, CookieContainer> _cookieContainers = new();
    private readonly ILogger<VtexCookieManager> _log;
    private readonly object _lock = new();

    public VtexCookieManager(ILogger<VtexCookieManager> log)
    {
        _log = log;
    }

    public void SetCookiesForHost(string host, string cookieString)
    {
        lock (_lock)
        {
            if (!_cookieContainers.ContainsKey(host))
            {
                _cookieContainers[host] = new CookieContainer();
            }

            var container = _cookieContainers[host];
            var uri = new Uri(host);

            // Parse las cookies del string
            var cookies = cookieString.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var cookieStr in cookies)
            {
                var parts = cookieStr.Trim().Split('=', 2);
                if (parts.Length == 2)
                {
                    try
                    {
                        var name = parts[0].Trim();
                        var value = parts[1].Trim();

                        // Limpiar caracteres problemáticos
                        if (name.StartsWith("*") || name.EndsWith("*"))
                        {
                            name = name.Trim('*');
                        }

                        var cookie = new Cookie(name, value, "/", uri.Host);
                        container.Add(cookie);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed to parse cookie: {Cookie}", cookieStr);
                    }
                }
            }

            _log.LogInformation("Set {Count} cookies for host {Host}", cookies.Length, host);
        }
    }

    public CookieContainer GetCookieContainer(string host)
    {
        lock (_lock)
        {
            if (_cookieContainers.TryGetValue(host, out var container))
            {
                return container;
            }

            // Si no tenemos cookies específicas, creamos un container con cookies por defecto
            var newContainer = new CookieContainer();
            _cookieContainers[host] = newContainer;

            // Establecer cookies mínimas para VTEX
            SetDefaultVtexCookies(host, newContainer);

            return newContainer;
        }
    }

    public void UpdateSegmentCookie(string host, int salesChannel, string? regionId = null)
    {
        lock (_lock)
        {
            var container = GetCookieContainer(host);
            var uri = new Uri(host);

            try
            {
                // Crear vtex_segment cookie con los valores necesarios
                var segmentData = new Dictionary<string, object>
                {
                    ["campaigns"] = null,
                    ["channel"] = salesChannel.ToString(),
                    ["priceTables"] = null,
                    ["utm_campaign"] = null,
                    ["utm_source"] = null,
                    ["utmi_campaign"] = null,
                    ["currencyCode"] = "ARS",
                    ["currencySymbol"] = "$",
                    ["countryCode"] = "ARG",
                    ["cultureInfo"] = "es-AR",
                    ["admin_cultureInfo"] = "es-AR",
                    ["channelPrivacy"] = "public"
                };

                if (!string.IsNullOrEmpty(regionId))
                {
                    segmentData["regionId"] = regionId;
                }

                var segmentJson = JsonSerializer.Serialize(segmentData);
                var segmentValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(segmentJson));

                var segmentCookie = new Cookie("vtex_segment", segmentValue, "/", uri.Host);
                container.Add(segmentCookie);

                _log.LogDebug("Updated vtex_segment cookie for {Host} with SC={SC}, Region={Region}",
                    host, salesChannel, regionId ?? "null");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to update segment cookie for {Host}", host);
            }
        }
    }

    public async Task WarmupCookiesAsync(HttpClient httpClient, string host, CancellationToken ct = default)
    {
        try
        {
            _log.LogInformation("Warming up cookies for {Host}", host);

            // 1. Visita inicial a la página principal
            await SafeGetAsync(httpClient, $"{host}/", ct);

            // 2. Endpoint de segment para obtener cookies de sesión
            await SafeGetAsync(httpClient, $"{host}/_v/segment", ct);

            // 3. Endpoint de checkout para cookies adicionales
            await SafeGetAsync(httpClient, $"{host}/api/checkout/pub/orderForm", ct);

            _log.LogInformation("Cookie warmup completed for {Host}", host);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cookie warmup failed for {Host}, continuing anyway", host);
        }
    }

    private void SetDefaultVtexCookies(string host, CookieContainer container)
    {
        var uri = new Uri(host);

        try
        {
            // Cookies básicas para VTEX
            container.Add(new Cookie("locale", "es-AR", "/", uri.Host));
            container.Add(new Cookie("VtexWorkspace", "master:-", "/", uri.Host));
            container.Add(new Cookie("vtex-search-anonymous", Guid.NewGuid().ToString("N"), "/", uri.Host));
            container.Add(new Cookie("vtex-search-session", Guid.NewGuid().ToString("N"), "/", uri.Host));

            // Cookie de binding address (importante para algunos retailers)
            var accountName = ExtractAccountName(host);
            if (!string.IsNullOrEmpty(accountName))
            {
                container.Add(new Cookie("vtex_binding_address", $"{accountName}.myvtex.com/", "/", uri.Host));
            }

            // Generar vtex_segment con valores por defecto
            UpdateSegmentCookie(host, 1); // Sales channel 1 por defecto

            _log.LogInformation("Set default VTEX cookies for {Host}", host);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set default cookies for {Host}", host);
        }
    }

    private static async Task SafeGetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            // No importa el status code, solo queremos las cookies
        }
        catch
        {
            // Ignorar errores de red
        }
    }

    private static string? ExtractAccountName(string host)
    {
        // Mapeo de hosts a account names de VTEX
        var hostMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "https://www.vea.com.ar", "veaargentina" },
            { "https://www.jumbo.com.ar", "jumboargentina" },
            { "https://www.disco.com.ar", "disco" },
            { "https://www.dia.com.ar", "dia" },
            { "https://www.coto.com.ar", "coto" },
            { "https://www.carrefour.com.ar", "carrefourar" },
            { "https://www.hiperlibertad.com.ar", "libertad" }
        };

        var normalizedHost = host.TrimEnd('/');
        return hostMap.TryGetValue(normalizedHost, out var accountName) ? accountName : null;
    }
}