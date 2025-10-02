// Ruta: ScrapeMart/Services/VtexSweepService.cs

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities;
using ScrapeMart.Storage;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// 🚀 SERVICIO CORREGIDO: Mapea sucursales físicas a pickup points online.
/// ✅ Usa Proxy de Bright Data.
/// ✅ Usa VtexCookieManager para sesiones por host.
/// ✅ Usa el payload de simulación que SÍ funciona.
/// </summary>
public sealed class VtexSweepService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<VtexSweepService> _log;
    private readonly string _sqlConn;
    private readonly AppDb _db;
    private readonly IVtexCookieManager _cookieManager;
    private readonly IConfiguration _config;

    private readonly ConcurrentDictionary<string, (string SkuId, string SellerId)> _probeCache = new();

    private static readonly JsonSerializerOptions Jso = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public VtexSweepService(
        IHttpClientFactory httpFactory,
        ILogger<VtexSweepService> log,
        IConfiguration cfg,
        AppDb appDb,
        IVtexCookieManager cookieManager)
    {
        _db = appDb;
        _httpFactory = httpFactory;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
        _cookieManager = cookieManager;
        _config = cfg;
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
            _log.LogDebug("Usando proxy para {Host}", host);
        }

        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        return client;
    }


    public async Task<int> SweepAsync(string? hostFilter = null, int[]? scCandidates = null, int? top = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(hostFilter))
        {
            var lastSuccessfulSweep = await _db.SweepLogs
                .Where(log => log.RetailerHost == hostFilter && log.SweepType == "PickupPoints" && log.Status == "Success")
                .OrderByDescending(log => log.CompletedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (lastSuccessfulSweep != null && (DateTime.UtcNow - lastSuccessfulSweep.CompletedAtUtc.Value).TotalDays < 7)
            {
                _log.LogInformation("SALTANDO SWEEP para {Host}. El último barrido exitoso fue hace menos de 7 días (el {Date}).",
                    hostFilter, lastSuccessfulSweep.CompletedAtUtc.Value.ToLocalTime());
                return 0;
            }
        }
        var rows = await LoadWorkAsync(hostFilter, top, ct);
        var logEntry = new SweepLog
        {
            RetailerHost = hostFilter ?? "ALL",
            SweepType = "PickupPoints",
            StartedAtUtc = DateTime.UtcNow,
            Status = "Running"
        };
        var totalOps = 0;
        try
        {
            _db.SweepLogs.Add(logEntry);
            await _db.SaveChangesAsync(ct);

            foreach (var r in rows)
            {
                using var http = CreateConfiguredHttpClient(r.Host);

                _log.LogInformation("🍪 Haciendo Warmup de cookies para {Host}", r.Host);
                await _cookieManager.WarmupCookiesAsync(http, r.Host, ct);

                int[] channelsToSweep = [.. r.SalesChannels
                                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     .Select(s => int.TryParse(s, out var i) ? i : -1)
                                     .Where(i => i != -1)];

                if (channelsToSweep.Length == 0)
                {
                    channelsToSweep = scCandidates ?? [1];
                }

                foreach (var sc in channelsToSweep)
                {
                    _cookieManager.UpdateSegmentCookie(r.Host, sc);

                    var pickupsFound = 0;
                    var sellersFound = 0;

                    if (r.Long.HasValue && r.Lat.HasValue)
                    {
                        var geo = $"{r.Long.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)};{r.Lat.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}";
                        var urlGeo = $"{r.Host}/api/checkout/pub/pickup-points?geoCoordinates={geo}&sc={sc}";
                        var found = await DiscoverPickupAsync(http, r, sc, "geo", r.Long, r.Lat, null, urlGeo, ct);
                        pickupsFound += found;

                        if (!string.IsNullOrWhiteSpace(r.Postal))
                        {
                            var urlPostal = $"{r.Host}/api/checkout/pub/pickup-points?postalCode={Uri.EscapeDataString(r.Postal!)}&countryCode=AR&sc={sc}";
                            pickupsFound += await DiscoverPickupAsync(http, r, sc, "postal", null, null, r.Postal, urlPostal, ct);
                        }

                        if (!string.IsNullOrWhiteSpace(r.Postal))
                        {
                            var urlRegions = $"{r.Host}/api/checkout/pub/regions?country=AR&postalCode={Uri.EscapeDataString(r.Postal!)}&sc={sc}";
                            sellersFound += await DiscoverRegionsAsync(http, r, sc, r.Postal!, urlRegions, ct);
                        }
                    }
                    if (pickupsFound == 0 && sellersFound == 0)
                    {
                        _log.LogInformation("No se encontraron pickups ni sellers para la sucursal {IdSucursal} de {Host}. Activando fallback por simulación...", r.IdSucursal, r.Host);
                        var probe = await GetProbeSkuAsync(http, r.Host, sc, ct);
                        if (probe is not null)
                        {
                            await DiscoverStoresViaSimulationAsync(http, r, sc, r.Postal, r.City, r.Province, null, probe.Value.SkuId, probe.Value.SellerId, ct);
                        }
                    }

                    totalOps++;
                }
            }
            logEntry.Status = "Success";
            logEntry.Notes = $"Se procesaron {totalOps} operaciones.";
        }
        catch (Exception ex)
        {
            logEntry.Status = "Failed";
            logEntry.Notes = ex.Message;
            _log.LogError(ex, "El barrido falló inesperadamente.");
            throw;
        }
        finally
        {
            logEntry.CompletedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return totalOps;
    }

    private async Task DiscoverStoresViaSimulationAsync(HttpClient http, WorkRow r, int sc, string? postal, string? city, string? province, string? regionId, string skuId, string sellerId, CancellationToken ct)
    {
        var baseUrl = new StringBuilder($"{r.Host}/api/checkout/pub/orderForms/simulation?sc={sc}");
        if (!string.IsNullOrWhiteSpace(regionId)) { baseUrl.Append($"&regionId={Uri.EscapeDataString(regionId)}"); }

        object payload;
        if (r.Lat.HasValue && r.Long.HasValue)
        {
            var addressPayload = new { country = "ARG", addressType = "search", addressId = "simulation", geoCoordinates = new[] { r.Long.Value, r.Lat.Value } };
            payload = new { country = "ARG", items = new[] { new { id = skuId, quantity = 1, seller = sellerId } }, shippingData = new { address = addressPayload, clearAddressIfPostalCodeNotFound = false, selectedAddresses = new[] { addressPayload } } };
        }
        else
        {
            payload = new { country = "ARG", postalCode = postal, items = new[] { new { id = skuId, quantity = 1, seller = sellerId } }, shippingData = new { address = new { addressType = "residential", country = "ARG", postalCode = postal, city = city, state = province } } };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.ToString())
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Jso), Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, ct);
        var lastStatus = (int)resp.StatusCode;
        var lastJson = await resp.Content.ReadAsStringAsync(ct);
        var found = await ParseAndPersistPickupsFromSimulationAsync(r, lastJson, ct);
        await InsertPickupDiscoveryAsync(r, sc, "sim", r.Long, r.Lat, postal, lastStatus, found, TruncateForDb(lastJson, 8000), ct);
    }

    private async Task<int> DiscoverPickupAsync(HttpClient http, WorkRow r, int sc, string method, double? lon, double? lat, string? postal, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        var status = (int)resp.StatusCode;
        var json = await resp.Content.ReadAsStringAsync(ct);
        var itemsFound = 0;

        try
        {
            using var doc = JsonDocument.Parse(json);

            JsonElement itemsArray;
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var items))
            {
                itemsArray = items;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                itemsArray = doc.RootElement;
            }
            else
            {
                await InsertPickupDiscoveryAsync(r, sc, method, lon, lat, postal, status, 0, TruncateForDb(json, 8000), ct);
                return 0;
            }

            if (itemsArray.ValueKind == JsonValueKind.Array)
            {
                itemsFound = itemsArray.GetArrayLength();
                foreach (var el in itemsArray.EnumerateArray())
                    await PersistPickupFromElementAsync(r, el, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pickup parse failed for {Url}", url);
        }

        await InsertPickupDiscoveryAsync(r, sc, method, lon, lat, postal, status, itemsFound, TruncateForDb(json, 8000), ct);
        return itemsFound;
    }

    private async Task PersistPickupFromElementAsync(WorkRow r, JsonElement el, CancellationToken ct)
    {
        if (!el.TryGetProperty("pickupPoint", out var pickupPointElement))
        {
            pickupPointElement = el;
        }

        var id = pickupPointElement.TryGetProperty("id", out var v) ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return;

        var friendly = pickupPointElement.TryGetProperty("friendlyName", out var fn) ? fn.GetString() : null;
        string? address = null;
        double? plat = null, plon = null;

        if (pickupPointElement.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.Object)
        {
            if (ad.TryGetProperty("street", out var st))
                address = st.GetString();

            if (ad.TryGetProperty("geoCoordinates", out var gc) && gc.ValueKind == JsonValueKind.Array && gc.GetArrayLength() >= 2)
            {
                if (gc[0].TryGetDouble(out var glon)) plon = glon;
                if (gc[1].TryGetDouble(out var glat)) plat = glat;
            }
        }

        if (!plat.HasValue && pickupPointElement.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
        {
            if (pos.TryGetProperty("latitude", out var latEl) && latEl.TryGetDouble(out var vlat)) plat = vlat;
            if (pos.TryGetProperty("longitude", out var lonEl) && lonEl.TryGetDouble(out var vlon)) plon = vlon;
        }

        await UpsertPickupPointAsync(r.Host, id!, friendly, address, plon, plat, r.IdBandera, r.IdComercio, r.IdSucursal, ct);
    }

    private async Task<int> DiscoverRegionsAsync(HttpClient http, WorkRow r, int sc, string postal, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        var status = (int)resp.StatusCode;
        var json = await resp.Content.ReadAsStringAsync(ct);
        var sellers = new List<(string id, string? name)>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            void Scan(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.NameEquals("sellers") && p.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var s in p.Value.EnumerateArray())
                            {
                                var sid = s.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                                var sname = s.TryGetProperty("name", out var n) ? n.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(sid)) sellers.Add((sid!, sname));
                            }
                        }
                        else { Scan(p.Value); }
                    }
                }
                else if (e.ValueKind == JsonValueKind.Array) { foreach (var it in e.EnumerateArray()) Scan(it); }
            }
            Scan(doc.RootElement);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Regions parse failed for {Url}", url); }

        foreach (var s in sellers.Distinct())
            await UpsertRegionSellerAsync(r.IdBandera, r.IdComercio, r.Host, "AR", postal, sc, s.id, s.name, ct);

        await InsertPickupDiscoveryAsync(r, sc, "regions", null, null, postal, status, sellers.Count, TruncateForDb(json, 8000), ct);
        return sellers.Count;
    }

    private async Task<int> ParseAndPersistPickupsFromSimulationAsync(WorkRow r, string json, CancellationToken ct)
    {
        var found = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pickupPoints", out var pps) && pps.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pps.EnumerateArray())
                {
                    await PersistPickupFromElementAsync(r, el, ct);
                    found++;
                }
            }
            if (doc.RootElement.TryGetProperty("logisticsInfo", out var li) && li.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in li.EnumerateArray())
                {
                    if (!l.TryGetProperty("slas", out var slas) || slas.ValueKind != JsonValueKind.Array) continue;
                    foreach (var sla in slas.EnumerateArray())
                    {
                        var isPickup = sla.TryGetProperty("deliveryChannel", out var dc) && dc.GetString()?.Contains("pickup", StringComparison.OrdinalIgnoreCase) == true;
                        if (!isPickup && (!sla.TryGetProperty("pickupStoreInfo", out var psi) || psi.ValueKind != JsonValueKind.Object)) continue;
                        string? pid = null, friendly = null, address = null;
                        double? lon = null, lat = null;
                        if (sla.TryGetProperty("pickupStoreInfo", out var pinfo) && pinfo.ValueKind == JsonValueKind.Object)
                        {
                            if (pinfo.TryGetProperty("friendlyName", out var fn)) friendly = fn.GetString();
                            if (pinfo.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object)
                            {
                                if (addr.TryGetProperty("addressId", out var aid)) pid = aid.GetString();
                                if (addr.TryGetProperty("street", out var st)) address = st.GetString();
                                if (addr.TryGetProperty("geoCoordinates", out var gc) && gc.ValueKind == JsonValueKind.Array && gc.GetArrayLength() >= 2)
                                {
                                    if (gc[0].TryGetDouble(out var glon)) lon = glon;
                                    if (gc[1].TryGetDouble(out var glat)) lat = glat;
                                }
                            }
                        }
                        if (string.IsNullOrWhiteSpace(pid) && sla.TryGetProperty("pickupPointId", out var ppid)) pid = ppid.GetString();
                        if (!string.IsNullOrWhiteSpace(pid))
                        {
                            await UpsertPickupPointAsync(r.Host, pid!, friendly, address, lon, lat, r.IdBandera, r.IdComercio, r.IdSucursal, ct);
                            found++;
                        }
                    }
                }
            }
        }
        catch { }
        return found;
    }

    private async Task<(string SkuId, string SellerId)?> GetProbeSkuAsync(HttpClient http, string host, int sc, CancellationToken ct)
    {
        if (_probeCache.TryGetValue(host, out var cached)) return cached;
        var url = $"{host}/api/catalog_system/pub/products/search?ft=a&_from=0&_to=10";
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Probe search failed {Status} for {Url}", (int)resp.StatusCode, url);
            return null;
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var prod in doc.RootElement.EnumerateArray())
            {
                if (!prod.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var it in items.EnumerateArray())
                {
                    var skuId = it.TryGetProperty("itemId", out var iid) ? iid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(skuId)) continue;
                    if (!it.TryGetProperty("sellers", out var sellers) || sellers.ValueKind != JsonValueKind.Array) continue;
                    foreach (var s in sellers.EnumerateArray())
                    {
                        var sid = s.TryGetProperty("sellerId", out var sv) ? sv.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(sid))
                        {
                            var result = (skuId!, sid!);
                            _probeCache[host] = result;
                            return result;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Probe parse failed for {Url}", url); }
        return null;
    }

    private async Task UpsertPickupPointAsync(string host, string pickupId, string? friendly, string? address, double? lon, double? lat, int idBandera, int idComercio, int idSucursal, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexPickupPoints AS T
USING (SELECT @host AS RetailerHost, @id AS PickupPointId) AS S
ON (T.RetailerHost = S.RetailerHost AND T.PickupPointId = S.PickupPointId)
WHEN MATCHED THEN UPDATE SET FriendlyName = @friendly, Address = @address, Lon = @lon, Lat = @lat, LastSeenUtc = SYSUTCDATETIME(), SourceIdBandera = @idBandera, SourceIdComercio = @idComercio, SourceIdSucursal = @idSucursal
WHEN NOT MATCHED THEN INSERT (RetailerHost, PickupPointId, FriendlyName, Address, Lon, Lat, FirstSeenUtc, LastSeenUtc, SourceIdBandera, SourceIdComercio, SourceIdSucursal)
VALUES (@host, @id, @friendly, @address, @lon, @lat, SYSUTCDATETIME(), SYSUTCDATETIME(), @idBandera, @idComercio, @idSucursal);";
        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@id", pickupId);
        cmd.Parameters.AddWithValue("@friendly", (object?)friendly ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@address", (object?)address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lon", (object?)lon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lat", (object?)lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@idBandera", idBandera);
        cmd.Parameters.AddWithValue("@idComercio", idComercio);
        cmd.Parameters.AddWithValue("@idSucursal", idSucursal);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertRegionSellerAsync(int idBandera, int idComercio, string host, string country, string postal, int sc, string sellerId, string? sellerName, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexRegionSellers AS T
USING (SELECT @idBandera AS IdBandera, @idComercio AS IdComercio, @host AS RetailerHost, @country AS CountryCode, @postal AS PostalCode, @sc AS SalesChannel, @sellerId AS SellerId) AS S
ON (T.IdBandera=S.IdBandera AND T.IdComercio=S.IdComercio AND T.RetailerHost=S.RetailerHost AND T.CountryCode=S.CountryCode AND T.PostalCode=S.PostalCode AND T.SalesChannel=S.SalesChannel AND T.SellerId=S.SellerId)
WHEN MATCHED THEN UPDATE SET SellerName=@sellerName, LastSeenUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (IdBandera, IdComercio, RetailerHost, CountryCode, PostalCode, SalesChannel, SellerId, SellerName, LastSeenUtc)
VALUES (@idBandera, @idComercio, @host, @country, @postal, @sc, @sellerId, @sellerName, SYSUTCDATETIME());";
        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@idBandera", idBandera);
        cmd.Parameters.AddWithValue("@idComercio", idComercio);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@country", country);
        cmd.Parameters.AddWithValue("@postal", postal);
        cmd.Parameters.AddWithValue("@sc", sc);
        cmd.Parameters.AddWithValue("@sellerId", sellerId);
        cmd.Parameters.AddWithValue("@sellerName", (object?)sellerName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertPickupDiscoveryAsync(WorkRow r, int sc, string method, double? lon, double? lat, string? postal, int httpStatus, int itemsFound, string rawJson, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.VtexPickupDiscovery
    (IdBandera, IdComercio, IdSucursal, RetailerHost, SalesChannel, Method, Lon, Lat, PostalCode, HttpStatus, ItemsFound, RawJson)
VALUES
    (@idBandera, @idComercio, @idSucursal, @host, @sc, @method, @lon, @lat, @postal, @status, @found, @raw);";
        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@idBandera", r.IdBandera);
        cmd.Parameters.AddWithValue("@idComercio", r.IdComercio);
        cmd.Parameters.AddWithValue("@idSucursal", r.IdSucursal);
        cmd.Parameters.AddWithValue("@host", r.Host);
        cmd.Parameters.AddWithValue("@sc", sc);
        cmd.Parameters.AddWithValue("@method", method);
        cmd.Parameters.AddWithValue("@lon", (object?)lon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lat", (object?)lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@postal", (object?)postal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", httpStatus);
        cmd.Parameters.AddWithValue("@found", itemsFound);
        cmd.Parameters.AddWithValue("@raw", (object?)rawJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<WorkRow>> LoadWorkAsync(string? hostFilter, int? top, CancellationToken ct)
    {
        var list = new List<WorkRow>();
        var sql = new StringBuilder(@"
SELECT " + (top.HasValue ? $"TOP ({top.Value})" : "") + @"
    r.SourceIdBandera AS IdBandera, 
    r.SourceIdComercio AS IdComercio,
    r.VtexHost AS Host,
    r.SalesChannels AS SalesChannels,
    s.StoreId AS IdSucursal,
    s.PostalCode AS Postal,
    s.Longitude AS Lon,
    s.Latitude AS Lat,
    s.City,
    s.Province
FROM dbo.Retailers r
JOIN dbo.Stores s ON r.RetailerId = s.RetailerId
WHERE r.IsActive=1 AND s.IsActive=1
");

        if (!string.IsNullOrWhiteSpace(hostFilter))
        {
            sql.Append("AND r.VtexHost = @host ");
        }

        sql.Append("ORDER BY r.VtexHost, s.StoreId;");
        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql.ToString(), cn);
        if (!string.IsNullOrWhiteSpace(hostFilter))
            cmd.Parameters.AddWithValue("@host", hostFilter!);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new WorkRow
            {
                IdBandera = rd.GetInt32(rd.GetOrdinal("IdBandera")),
                IdComercio = rd.GetInt32(rd.GetOrdinal("IdComercio")),
                Host = rd.GetString(rd.GetOrdinal("Host")),
                SalesChannels = rd.GetString(rd.GetOrdinal("SalesChannels")),
                IdSucursal = (int)rd.GetInt64(rd.GetOrdinal("IdSucursal")),
                Postal = rd.IsDBNull(rd.GetOrdinal("Postal")) ? null : rd.GetString(rd.GetOrdinal("Postal")),
                Long = rd.IsDBNull(rd.GetOrdinal("Lon")) ? null : (double?)rd.GetDecimal(rd.GetOrdinal("Lon")),
                Lat = rd.IsDBNull(rd.GetOrdinal("Lat")) ? null : (double?)rd.GetDecimal(rd.GetOrdinal("Lat")),
                City = rd.IsDBNull(rd.GetOrdinal("City")) ? null : rd.GetString(rd.GetOrdinal("City")),
                Province = rd.IsDBNull(rd.GetOrdinal("Province")) ? null : rd.GetString(rd.GetOrdinal("Province")),
            });
        }
        return list;
    }

    private static string TruncateForDb(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max);

    private sealed class WorkRow
    {
        public int IdBandera { get; set; }
        public int IdComercio { get; set; }
        public string Host { get; set; } = default!;
        public string SalesChannels { get; set; } = default!;
        public int IdSucursal { get; set; }
        public string? Postal { get; set; }
        public double? Long { get; set; }
        public double? Lat { get; set; }
        public string? City { get; set; }
        public string? Province { get; set; }
    }
}