using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

public sealed class VtexSweepService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<VtexSweepService> _log;
    private readonly string _sqlConn;

    // Cache por host del SKU y seller de prueba para no pegar mil veces al search
    private readonly ConcurrentDictionary<string, (string SkuId, string SellerId)> _probeCache = new();

    private static readonly JsonSerializerOptions Jso = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public VtexSweepService(IHttpClientFactory http, ILogger<VtexSweepService> log, IConfiguration cfg)
    {
        _http = http;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
    }

    public async Task<int> SweepAsync(string? hostFilter = null, int[]? scCandidates = null, int? top = null, CancellationToken ct = default)
    {
        scCandidates ??= new[] { 1, 2, 3, 4 };

        var rows = await LoadWorkAsync(hostFilter, top, ct);

        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ScrapeMart/1.0 (+VTEX sweep)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var totalOps = 0;

        foreach (var r in rows)
        {
            foreach (var sc in scCandidates)
            {
                // GEO pickup-points
                if (r.Long.HasValue && r.Lat.HasValue)
                {
                    var geo = $"{r.Long.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)};{r.Lat.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}";
                    var urlGeo = $"{r.Host}/api/checkout/pub/pickup-points?geoCoordinates={geo}&sc={sc}";
                    totalOps += await DiscoverPickupAsync(http, r, sc, "geo", r.Long, r.Lat, null, urlGeo, ct);
                }

                // POSTAL pickup-points + regions
                if (!string.IsNullOrWhiteSpace(r.Postal))
                {
                    var urlPostal = $"{r.Host}/api/checkout/pub/pickup-points?postalCode={Uri.EscapeDataString(r.Postal!)}&countryCode=AR&sc={sc}";
                    totalOps += await DiscoverPickupAsync(http, r, sc, "postal", null, null, r.Postal, urlPostal, ct);

                    var urlRegions = $"{r.Host}/api/checkout/pub/regions?country=AR&postalCode={Uri.EscapeDataString(r.Postal!)}&sc={sc}";
                    totalOps += await DiscoverRegionsAsync(http, r, sc, r.Postal!, urlRegions, trySimulationFallback: true, ct);
                }
            }
        }

        return totalOps;
    }

    // ----------------------------
    // Descubrimiento pickup-points
    // ----------------------------
    private async Task<int> DiscoverPickupAsync(HttpClient http, WorkRow r, int sc, string method, double? lon, double? lat, string? postal, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        var status = (int)resp.StatusCode;
        var json = await resp.Content.ReadAsStringAsync(ct);
        var itemsFound = 0;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                itemsFound = doc.RootElement.GetArrayLength();
                foreach (var el in doc.RootElement.EnumerateArray())
                    await PersistPickupFromElementAsync(r, el, ct);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("items", out var items) &&
                     items.ValueKind == JsonValueKind.Array)
            {
                // Algunas cuentas devuelven { paging, items: [] }
                itemsFound = items.GetArrayLength();
                foreach (var el in items.EnumerateArray())
                    await PersistPickupFromElementAsync(r, el, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pickup parse failed for {Url}", url);
        }

        await InsertPickupDiscoveryAsync(r, sc, method, lon, lat, postal, status, itemsFound, TruncateForDb(json, 8000), ct);
        return 1;
    }

    private async Task PersistPickupFromElementAsync(WorkRow r, JsonElement el, CancellationToken ct)
    {
        var id = el.TryGetProperty("id", out var v) ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return;

        var friendly = el.TryGetProperty("friendlyName", out var fn) ? fn.GetString() : null;

        string? address = null;
        if (el.TryGetProperty("address", out var ad))
        {
            if (ad.ValueKind == JsonValueKind.String) address = ad.GetString();
            else if (ad.ValueKind == JsonValueKind.Object && ad.TryGetProperty("street", out var st))
                address = st.GetString();
        }

        double? plat = null, plon = null;
        if (el.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
        {
            if (pos.TryGetProperty("latitude", out var latEl) && latEl.TryGetDouble(out var vlat)) plat = vlat;
            if (pos.TryGetProperty("longitude", out var lonEl) && lonEl.TryGetDouble(out var vlon)) plon = vlon;
        }
        else if (el.TryGetProperty("address", out var addrObj) &&
                 addrObj.ValueKind == JsonValueKind.Object &&
                 addrObj.TryGetProperty("geoCoordinates", out var gc) &&
                 gc.ValueKind == JsonValueKind.Array &&
                 gc.GetArrayLength() >= 2)
        {
            // VTEX suele devolver [lon, lat]
            if (gc[0].TryGetDouble(out var glon)) plon = glon;
            if (gc[1].TryGetDouble(out var glat)) plat = glat;
        }

        await UpsertPickupPointAsync(r.Host, id!, friendly, address, plon, plat, r.IdBandera, r.IdComercio, r.IdSucursal, ct);
    }

    // ----------------------------
    // REGIONS + Fallback simulación
    // ----------------------------
    private async Task<int> DiscoverRegionsAsync(HttpClient http, WorkRow r, int sc, string postal, string url, bool trySimulationFallback, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        var status = (int)resp.StatusCode;
        var json = await resp.Content.ReadAsStringAsync(ct);

        var sellers = new List<(string id, string? name)>();
        var regionIds = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(json);

            void Scan(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.NameEquals("id") && p.Value.ValueKind == JsonValueKind.String)
                            regionIds.Add(p.Value.GetString()!);

                        if (p.NameEquals("sellers") && p.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var s in p.Value.EnumerateArray())
                            {
                                var sid = s.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                                var sname = s.TryGetProperty("name", out var n) ? n.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(sid)) sellers.Add((sid!, sname));
                            }
                        }
                        else
                        {
                            Scan(p.Value);
                        }
                    }
                }
                else if (e.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in e.EnumerateArray()) Scan(it);
                }
            }

            Scan(doc.RootElement);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Regions parse failed for {Url}", url);
        }

        foreach (var s in sellers.Distinct())
            await UpsertRegionSellerAsync(r.IdBandera, r.IdComercio, r.Host, "AR", postal, sc, s.id, s.name, ct);

        await InsertPickupDiscoveryAsync(r, sc, "regions", null, null, postal, status, sellers.Count, TruncateForDb(json, 8000), ct);

        // Fallback: si NO hay sellers, probamos simulación con un SKU de prueba
        if (trySimulationFallback && sellers.Count == 0 && regionIds.Count > 0)
        {
            var probe = await GetProbeSkuAsync(http, r.Host, sc, ct);
            if (probe is not null)
            {
                foreach (var regionId in regionIds.Distinct())
                {
                    await DiscoverStoresViaSimulationAsync(http, r, sc, postal, regionId, probe.Value.SkuId, probe.Value.SellerId, ct);
                }
            }
        }

        return 1;
    }
    private async Task DiscoverStoresViaSimulationAsync(
    HttpClient http, WorkRow r, int sc, string postal, string regionId,
    string skuId, string sellerId, CancellationToken ct)
    {
        var baseUrl = $"{r.Host}/api/checkout/pub/orderForms/simulation?sc={sc}&regionId={Uri.EscapeDataString(regionId)}";

        // 3 intentos con payloads distintos
        var attempts = new List<object>
    {
        // A) Sencillo (ARG)
        new {
            country = "ARG",
            postalCode = postal,
            items = new[] { new { id = skuId, quantity = 1, seller = sellerId } }
        },
        // B) Sencillo (AR)
        new {
            country = "AR",
            postalCode = postal,
            items = new[] { new { id = skuId, quantity = 1, seller = sellerId } }
        },
        // C) Con shippingData + address.regionId (muy común)
        new {
            country = "ARG",
            postalCode = postal,
            items = new[] { new { id = skuId, quantity = 1, seller = sellerId } },
            shippingData = new {
                address = new {
                    addressType = "residential",
                    country = "ARG",
                    postalCode = postal,
                    regionId = regionId,
                    city = "",
                    state = "",
                    street = "",
                    number = "",
                    complement = "",
                    receiverName = ""
                },
                selectedAddresses = Array.Empty<object>()
            }
        }
    };

        var totalFound = 0;
        int lastStatus = 0;
        string lastJson = "";

        foreach (var payload in attempts)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, Jso), Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);
            lastStatus = (int)resp.StatusCode;
            lastJson = await resp.Content.ReadAsStringAsync(ct);

            var found = await ParseAndPersistPickupsFromSimulationAsync(r, lastJson, ct);
            totalFound += found;

            // Logueamos cada intento (te sirve para auditar qué payload funcionó)
            await InsertPickupDiscoveryAsync(r, sc, "sim", null, null, postal, lastStatus, found, TruncateForDb(lastJson, 8000), ct);

            if (found > 0 || resp.IsSuccessStatusCode)
                break; // si ya encontramos algo, no hace falta seguir probando
        }
    }

    private async Task<int> ParseAndPersistPickupsFromSimulationAsync(WorkRow r, string json, CancellationToken ct)
    {
        var found = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);

            // 1) pickupPoints explícitos
            if (doc.RootElement.TryGetProperty("pickupPoints", out var pps) && pps.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pps.EnumerateArray())
                {
                    await PersistPickupFromElementAsync(r, el, ct);
                    found++;
                }
            }

            // 2) SLAs con pickupStoreInfo
            if (doc.RootElement.TryGetProperty("logisticsInfo", out var li) && li.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in li.EnumerateArray())
                {
                    if (!l.TryGetProperty("slas", out var slas) || slas.ValueKind != JsonValueKind.Array) continue;

                    foreach (var sla in slas.EnumerateArray())
                    {
                        var isPickup = sla.TryGetProperty("deliveryChannel", out var dc)
                                       && dc.GetString()?.Contains("pickup", StringComparison.OrdinalIgnoreCase) == true;

                        if (!isPickup && (!sla.TryGetProperty("pickupStoreInfo", out var psi) || psi.ValueKind != JsonValueKind.Object))
                            continue;

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
                        if (string.IsNullOrWhiteSpace(pid) && sla.TryGetProperty("pickupPointId", out var ppid))
                            pid = ppid.GetString();

                        if (!string.IsNullOrWhiteSpace(pid))
                        {
                            await UpsertPickupPointAsync(r.Host, pid!, friendly, address, lon, lat, r.IdBandera, r.IdComercio, r.IdSucursal, ct);
                            found++;
                        }
                    }
                }
            }
        }
        catch
        {
            // swallow: ya se loguea el raw JSON en VtexPickupDiscovery
        }
        return found;
    }

    private async Task<(string SkuId, string SellerId)?> GetProbeSkuAsync(HttpClient http, string host, int sc, CancellationToken ct)
    {
        if (_probeCache.TryGetValue(host, out var cached)) return cached;

        // Buscamos algo muy genérico: ft=a (10 primeros). Tomamos el primer SKU con seller.
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Probe parse failed for {Url}", url);
        }

        return null;
    }
    private async Task UpsertPickupPointAsync(string host, string pickupId, string? friendly, string? address, double? lon, double? lat,
        int idBandera, int idComercio, int idSucursal, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexPickupPoints AS T
USING (SELECT @host AS RetailerHost, @id AS PickupPointId) AS S
ON (T.RetailerHost = S.RetailerHost AND T.PickupPointId = S.PickupPointId)
WHEN MATCHED THEN UPDATE SET
    FriendlyName = @friendly,
    Address = @address,
    Lon = @lon,
    Lat = @lat,
    LastSeenUtc = SYSUTCDATETIME(),
    SourceIdBandera = @idBandera,
    SourceIdComercio = @idComercio,
    SourceIdSucursal = @idSucursal
WHEN NOT MATCHED THEN INSERT
    (RetailerHost, PickupPointId, FriendlyName, Address, Lon, Lat, FirstSeenUtc, LastSeenUtc, SourceIdBandera, SourceIdComercio, SourceIdSucursal)
VALUES
    (@host, @id, @friendly, @address, @lon, @lat, SYSUTCDATETIME(), SYSUTCDATETIME(), @idBandera, @idComercio, @idSucursal);";

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
SELECT " + (top.HasValue ? $"TOP ({top.Value})" : "TOP (10000)") + @"
    b.IdBandera, b.IdComercio,
    cfg.RetailerHost AS Host, cfg.DefaultSalesChannel AS SC,
    s.IdSucursal,
    NULLIF(LTRIM(RTRIM(s.SucursalesCodigoPostal)), '') AS Postal,
    CASE WHEN s.SucursalesLongitud = 0 THEN NULL ELSE s.SucursalesLongitud END AS Lon,
    CASE WHEN s.SucursalesLatitud  = 0 THEN NULL ELSE s.SucursalesLatitud  END AS Lat
FROM dbo.Banderas b
JOIN dbo.VtexRetailersConfig cfg ON cfg.IdBandera=b.IdBandera AND cfg.IdComercio=b.IdComercio AND cfg.Enabled=1
JOIN dbo.Sucursales s ON s.IdBandera=b.IdBandera AND s.IdComercio=b.IdComercio
");

        if (!string.IsNullOrWhiteSpace(hostFilter))
            sql.Append("WHERE cfg.RetailerHost = @host ");

        sql.Append("ORDER BY b.IdBandera, b.IdComercio, s.IdSucursal;");

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
                IdBandera = rd.GetInt32(0),
                IdComercio = rd.GetInt32(1),
                Host = rd.GetString(2),
                SC = rd.GetInt32(3),
                IdSucursal = rd.GetInt32(4),
                Postal = rd.IsDBNull(5) ? null : rd.GetString(5),
                Long = rd.IsDBNull(6) ? null : rd.GetDouble(6),
                Lat = rd.IsDBNull(7) ? null : rd.GetDouble(7),
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
        public int SC { get; set; }
        public int IdSucursal { get; set; }
        public string? Postal { get; set; }
        public double? Long { get; set; }
        public double? Lat { get; set; }
    }
}
