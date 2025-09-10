using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScrapeMart.Services;

public sealed class VtexCatalogClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // Simplificamos el constructor, ya no depende de VtexOptions
    public VtexCatalogClient(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    }

    // Ahora todos los métodos reciben el 'host' al que deben apuntar
    public async Task<JsonArray> GetCategoryTreeAsync(string host, int depth, CancellationToken ct)
    {
        var url = $"{host.TrimEnd('/')}/api/catalog_system/pub/category/tree/{depth}";
        var doc = await _http.GetFromJsonAsync<JsonArray>(url, _json, ct) ?? new JsonArray();
        return doc;
    }

    public async IAsyncEnumerable<JsonObject> GetProductsByCategoryAsync(string host, int categoryId, int pageSize, int? maxPages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var from = 0;
        var page = 0;
        while (true)
        {
            if (ct.IsCancellationRequested) yield break;
            if (maxPages is not null && page >= maxPages) yield break;

            var to = from + pageSize - 1;
            var url = $"{host.TrimEnd('/')}/api/catalog_system/pub/products/search/?fq=C:{categoryId}&_from={from}&_to={to}";
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) yield break;

            var txt = await res.Content.ReadAsStringAsync(ct);
            var arr = JsonNode.Parse(txt) as JsonArray;
            if (arr is null || arr.Count == 0) yield break;

            foreach (var n in arr)
                if (n is JsonObject o) yield return o;

            if (arr.Count < pageSize) yield break;
            from += pageSize;
            page++;
        }
    }
}