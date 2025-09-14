using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Text;
using System.Text.Json.Nodes;

namespace ScrapeMart.Services;

public sealed class TargetedScrapingService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TargetedScrapingService> _log;

    public TargetedScrapingService(IServiceProvider serviceProvider, ILogger<TargetedScrapingService> log)
    {
        _serviceProvider = serviceProvider;
        _log = log;
    }

    public async Task ScrapeByEanListAsync(string host, CancellationToken ct)
    {
        _log.LogInformation("Iniciando scraping dirigido por EAN (método GraphQL) para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var syncService = scope.ServiceProvider.GetRequiredService<CatalogSyncService>();
        var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("vtexSession");

        var eansToScrape = await db.ProductsToTrack.AsNoTracking().Select(p => p.EAN).ToListAsync(ct);
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
                // --- ¡ESTA ES LA LÓGICA BASADA EN TU URL! ---
                var variablesJson = $"{{\"fullText\":\"{ean}\"}}";
                var base64Variables = Convert.ToBase64String(Encoding.UTF8.GetBytes(variablesJson));

                // Usamos los parámetros fijos de la URL que encontraste
                const string sha256Hash = "0a16ef70d196958d57b5bd650cb3c3486598d7054b3e6b8c6376afc94d0ad621";
                const string sender = "vtex.store-resources@0.x";
                const string provider = "vtex.search-graphql@0.x";

                var extensions = $"{{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"{sha256Hash}\",\"sender\":\"{sender}\",\"provider\":\"{provider}\"}},\"variables\":\"{base64Variables}\"}}";

                var url = $"{host.TrimEnd('/')}/_v/segment/graphql/v1?workspace=master&maxAge=medium&operationName=autocompleteSearchSuggestions&variables=%7B%7D&extensions={Uri.EscapeDataString(extensions)}";

                var responseString = await httpClient.GetStringAsync(url, ct);

                // El JSON de GraphQL viene anidado, tenemos que desarmarlo
                var gqlResponse = JsonNode.Parse(responseString);
                var productNode = gqlResponse?["data"]?["productSearch"]?["products"]?.AsArray().FirstOrDefault()?.AsObject();

                if (productNode is null)
                {
                    _log.LogWarning("No se encontró producto para el EAN {EAN} en la respuesta de GraphQL.", ean);
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
                _log.LogError(ex, "Falló la búsqueda GraphQL para el EAN {EAN}", ean);
            }
        }
        _log.LogInformation("Scraping dirigido finalizado. Se encontraron y guardaron {Count} productos.", productsFound);
    }
}