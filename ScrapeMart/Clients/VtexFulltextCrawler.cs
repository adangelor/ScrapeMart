using System.Text.Json;

namespace ScrapeMart.Clients;

public interface IVtexFulltextSink
{
    // Persistí el JSON como quieras (parse/UPSERT). Debe devolver cuántos productos se guardaron.
    Task<int> PersistAsync(string host, string json, CancellationToken ct);
}

public sealed class VtexFulltextCrawler
{
    private readonly IVtexFulltextSink _sink;
    private readonly ILogger<VtexFulltextCrawler> _log;

    public VtexFulltextCrawler(IVtexFulltextSink sink, ILogger<VtexFulltextCrawler> log)
    {
        _sink = sink;
        _log = log;
    }

    public async Task WarmupAsync(HttpClient http, string host, CancellationToken ct)
    {
        // Levanta cookies de segment/ABTest/etc
        await SafeGet(http, $"{host}/", ct);
        await SafeGet(http, $"{host}/_v/segment", ct);
        // Algunos hosts piden otra request para soltar cookies; si falla, no importa.
        await SafeGet(http, $"{host}/api/checkout/pub/orderForm", ct);
    }

    public async Task<(int parsed, int status)> SweepOnceAsync(
        HttpClient http, string host, string query, int from, int to, CancellationToken ct)
    {
        var url = $"{host}/api/catalog_system/pub/products/search" +
                  $"?ft={Uri.EscapeDataString(query)}&_from={from}&_to={to}";

        var (status, body) = await GetWithHeadersAsync(http, host, url, ct);

        // Reintento tras warmup si pega 401/403
        if (status is 401 or 403)
        {
            _log.LogInformation("Fulltext 401/403 en {Host}. Haciendo warmup y reintentando…", host);
            await WarmupAsync(http, host, ct);
            (status, body) = await GetWithHeadersAsync(http, host, url, ct);
        }

        if (status is 200 or 206)
        {
            // Podés filtrar por ValueKind == Array, pero VTEX devuelve array.
            return (await _sink.PersistAsync(host, body, ct), status);
        }

        _log.LogWarning("Fulltext {Status} en {Host} para {Url}", status, host, url);
        return (0, status);
    }

    private static async Task<(int status, string body)> GetWithHeadersAsync(
        HttpClient http, string host, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(host + "/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");

        using var resp = await http.SendAsync(req, ct);
        var status = (int)resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (status, body);
    }

    private static async Task SafeGet(HttpClient http, string url, CancellationToken ct)
    {
        try { using var _ = await http.GetAsync(url, ct); } catch { /* no-op */ }
    }
}

public sealed class VtexFulltextSink : IVtexFulltextSink
{
    private readonly ILogger<VtexFulltextSink> _log;
    public VtexFulltextSink(ILogger<VtexFulltextSink> log) => _log = log;

    public Task<int> PersistAsync(string host, string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return Task.FromResult(doc.RootElement.GetArrayLength());

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PersistAsync parse failed for {Host}", host);
            return Task.FromResult(0);
        }
    }
}

public sealed record ScanArgs(string[] Hosts, string Query, int From = 0, int To = 49);


