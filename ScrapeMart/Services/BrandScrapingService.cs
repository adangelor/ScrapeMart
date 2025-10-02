// Ruta: ScrapeMart/Services/BrandScrapingService.cs

using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScrapeMart.Services;

/// <summary>
/// 🚀 SERVICIO CORREGIDO: Scraping por prefijo de marca.
/// ✅ Usa Proxy de Bright Data.
/// ✅ Usa VtexCookieManager para sesiones por host.
/// </summary>
public sealed class BrandScrapingService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BrandScrapingService> _log;
    // --- DEPENDENCIAS AÑADIDAS ---
    private readonly IVtexCookieManager _cookieManager;
    private readonly IConfiguration _config;

    public BrandScrapingService(
        IServiceProvider serviceProvider,
        ILogger<BrandScrapingService> log,
        IVtexCookieManager cookieManager, // Inyectada
        IConfiguration config)           // Inyectada
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _cookieManager = cookieManager;
        _config = config;
    }

    /// <summary>
    /// Crea un HttpClient configurado con Proxy y Cookies para un host específico.
    /// </summary>
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


    public async Task ScrapeTrackedBrandsAsync(string host, CancellationToken ct)
    {
        _log.LogInformation("Iniciando scraping por PREFIJO DE MARCA (método productSuggestions) para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var syncService = scope.ServiceProvider.GetRequiredService<CatalogSyncService>();

        // --- USA EL CLIENTE CONFIGURADO ---
        using var httpClient = CreateConfiguredHttpClient(host);
        await _cookieManager.WarmupCookiesAsync(httpClient, host, ct); // Calentamos las cookies

        var allEans = await db.ProductsToTrack.AsNoTracking().Select(p => p.EAN).ToListAsync(ct);
        var brandPrefixes = allEans.Select(ean => ean.Length > 8 ? ean[..8] : ean)
                                   .Distinct()
                                   .ToList();

        _log.LogInformation("Se buscarán {PrefixCount} prefijos de marca.", brandPrefixes.Count);

        int totalProductsSaved = 0;
        foreach (var prefix in brandPrefixes)
        {
            if (ct.IsCancellationRequested) break;

            _log.LogInformation("Buscando productos para el prefijo: {Prefix}...", prefix);
            try
            {
                var variablesObject = new
                {
                    productOriginVtex = true,
                    simulationBehavior = "default",
                    hideUnavailableItems = false,
                    fullText = prefix
                };
                var variablesJson = JsonSerializer.Serialize(variablesObject);
                var base64Variables = Convert.ToBase64String(Encoding.UTF8.GetBytes(variablesJson));

                const string operationName = "productSuggestions";
                const string sha256Hash = "78c2d5bdd2f0132a4acfef3449f327b7a264d63761533531f31daf3ee0dff155";
                const string sender = "vtex.store-resources@0.x";
                const string provider = "vtex.search-graphql@0.x";

                var extensions = $"{{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"{sha256Hash}\",\"sender\":\"{sender}\",\"provider\":\"{provider}\"}},\"variables\":\"{base64Variables}\"}}";
                var url = $"{host.TrimEnd('/')}/_v/segment/graphql/v1?workspace=master&operationName={operationName}&variables=%7B%7D&extensions={Uri.EscapeDataString(extensions)}";

                var responseString = await httpClient.GetStringAsync(url, ct);

                var gqlResponse = JsonNode.Parse(responseString);
                var productsArray = gqlResponse?["data"]?["productSuggestions"]?["products"]?.AsArray();

                if (productsArray is null || productsArray.Count == 0)
                {
                    _log.LogWarning("No se encontraron productos para el prefijo {Prefix}", prefix);
                    continue;
                }

                foreach (var productNode in productsArray.OfType<JsonObject>())
                {
                    var (processed, _) = await syncService.ProcessSingleProductNodeAsync(host, productNode, ct);
                    if (processed > 0)
                    {
                        totalProductsSaved++;
                    }
                }
                _log.LogInformation("Prefijo {Prefix} procesado. Se encontraron y guardaron {Count} productos.", prefix, productsArray.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falló la búsqueda para el prefijo {Prefix}", prefix);
            }
        }
        _log.LogInformation("Scraping por marca finalizado. Total de productos guardados: {Count}", totalProductsSaved);
    }
}