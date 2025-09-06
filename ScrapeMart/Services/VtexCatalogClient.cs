using Microsoft.Extensions.Options;
using ScrapeMart.Entities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScrapeMart.Services
{
    public sealed class VtexCatalogClient
    {
        private readonly HttpClient _http;
        private readonly VtexOptions _opt;
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public VtexCatalogClient(HttpClient http, IOptions<VtexOptions> opt)
        {
            _http = http;
            _opt = opt.Value;
            _http.BaseAddress = _opt.BaseUri;
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VtexIngestor/1.0 (+.NET 9)");
        }

        public async Task<JsonArray> GetCategoryTreeAsync(int depth)
        {
            var url = $"/api/catalog_system/pub/category/tree/{depth}";
            var doc = await _http.GetFromJsonAsync<JsonArray>(url, _json) ?? new JsonArray();
            return doc;
        }

        public async IAsyncEnumerable<JsonObject> GetProductsByCategoryAsync(int categoryId, int pageSize, int? maxPages = null)
        {
            var from = 0;
            var page = 0;
            while (true)
            {
                if (maxPages is not null && page >= maxPages) yield break;
                var to = from + pageSize - 1;
                var url = $"/api/catalog_system/pub/products/search/?fq=C:{categoryId}&_from={from}&_to={to}";
                using var res = await _http.GetAsync(url);
                if (!res.IsSuccessStatusCode) yield break;

                var txt = await res.Content.ReadAsStringAsync();
                var arr = JsonNode.Parse(txt) as JsonArray;
                if (arr is null || arr.Count == 0) yield break;

                foreach (var n in arr)
                    if (n is JsonObject o) yield return o;

                if (arr.Count < pageSize) yield break;
                from += pageSize;
                page++;
            }
        }

        public async Task<JsonArray?> GetProductsSearchAsync(string query, int from, int to)
        {
            var url = $"/api/catalog_system/pub/products/search/?ft={Uri.EscapeDataString(query)}&_from={from}&_to={to}";
            return await _http.GetFromJsonAsync<JsonArray>(url, _json);
        }
    }
}
