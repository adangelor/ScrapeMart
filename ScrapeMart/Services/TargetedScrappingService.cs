// Ruta: ScrapeMart/Services/TargetedScrapingService.cs

using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScrapeMart.Services;

/// <summary>
/// 🚀 SERVICIO RECONTRA-CORREGIDO: Scraping dirigido por EAN.
/// ✅ Abandona el GraphQL frágil y usa la API REST estándar (/api/catalog_system/pub/products/search).
/// ✅ Usa Proxy de Bright Data.
/// ✅ Usa VtexCookieManager para sesiones por host.
/// </summary>
public sealed class TargetedScrapingService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TargetedScrapingService> _log;
    private readonly IVtexCookieManager _cookieManager;
    private readonly IConfiguration _config;

    public TargetedScrapingService(
        IServiceProvider serviceProvider,
        ILogger<TargetedScrapingService> log,
        IVtexCookieManager cookieManager,
        IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _cookieManager = cookieManager;
        _config = config;
    }

    private HttpClient CreateConfiguredHttpClient(string host)
    {
        var cookieContainer = _cookieManager.GetCookieContainer(host);
        var proxyConfig = _config.GetSection("Proxy");

        var handler = new HttpClientHandler()
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var proxyUrl = proxyConfig["Url"];
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            var proxy = new WebProxy(new Uri(proxyUrl));
            var username = proxyConfig["Username"];
            if (!string.IsNullOrEmpty(username))
            {
                proxy.Credentials = new NetworkCredential(username, proxyConfig["Password"]);
            }
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        return client;
    }

    public async Task ScrapeByEanListAsync(string host, CancellationToken ct)
    {
        _log.LogInformation("Iniciando scraping dirigido por EAN (método REST API) para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var syncService = scope.ServiceProvider.GetRequiredService<CatalogSyncService>();

        using var httpClient = CreateConfiguredHttpClient(host);
        await _cookieManager.WarmupCookiesAsync(httpClient, host, ct);

        var eansToScrape = await db.ProductsToTrack
            .Where(p => p.Track == true)
            .AsNoTracking()
            .Select(p => p.EAN)
            .ToListAsync(ct);

        _log.LogInformation("Se buscarán {EanCount} EANs de la tabla de seguimiento.", eansToScrape.Count);

        int productsFound = 0;
        foreach (var ean in eansToScrape)
        {
            if (ct.IsCancellationRequested)
            {
                _log.LogWarning("El scraping dirigido fue cancelado.");
                break;
            }

            try
            {
                // --- 👇 EL CAMBIO ESTÁ ACÁ: USAMOS LA API REST DIRECTA 👇 ---
                var searchUrl = $"{host.TrimEnd('/')}/api/catalog_system/pub/products/search?ft={ean}&_from=0&_to=0";

                var response = await httpClient.GetAsync(searchUrl, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("La búsqueda para el EAN {EAN} falló con status {StatusCode}", ean, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);

                if (string.IsNullOrEmpty(json) || json == "[]")
                {
                    _log.LogWarning("No se encontró producto para el EAN {EAN} en la respuesta de la API REST.", ean);
                    continue;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    _log.LogWarning("La respuesta para el EAN {EAN} no es un array de productos válido.", ean);
                    continue;
                }

                // Tomamos el primer producto del array de resultados
                var productNode = JsonObject.Parse(doc.RootElement[0].GetRawText())?.AsObject();
                // --- 👆 FIN DEL CAMBIO 👆 ---

                if (productNode is null)
                {
                    _log.LogWarning("No se pudo parsear el nodo del producto para el EAN {EAN}", ean);
                    continue;
                }

                var (processed, _) = await syncService.ProcessSingleProductNodeAsync(host, productNode, ct);
                if (processed > 0)
                {
                    productsFound++;
                    _log.LogInformation("Producto encontrado y guardado para EAN {EAN}", ean);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falló la búsqueda REST para el EAN {EAN}", ean);
            }
        }
        _log.LogInformation("Scraping dirigido finalizado. Se encontraron y guardaron {Count} productos.", productsFound);
    }
}