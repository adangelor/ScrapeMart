// File: Services/VtexPublicClient.cs
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

public sealed class VtexPublicClient
{
    // -----------------------------
    //  Infra y estado opcional
    // -----------------------------
    private readonly IHttpClientFactory? _httpFactory;
    private readonly HttpClient? _httpClient;
    private readonly string? _host;          // si querés usar el cliente "stateful"
    private readonly int _defaultSc = 1;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // Ctor sin args (sirve si llamás las variantes que reciben HttpClient)
    public VtexPublicClient() { }

    // Ctor con fábrica (podés usar las variantes "sin HttpClient" y se resuelve "vtexSession")
    public VtexPublicClient(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    // Ctor con HttpClient directo
    public VtexPublicClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _host = httpClient.BaseAddress?.ToString()?.TrimEnd('/');
    }

    // Ctor "stateful": fija host y salesChannel por defecto (opcionalmente con fábrica)
    public VtexPublicClient(string host, int defaultSalesChannel = 1, IHttpClientFactory? httpFactory = null)
    {
        _host = host.TrimEnd('/');
        _defaultSc = defaultSalesChannel;
        _httpFactory = httpFactory;
    }

    // Helpers
    private HttpClient GetClient(string name = "vtexSession")
    {
        if (_httpClient is not null)
            return _httpClient;

        if (_httpFactory is null)
            throw new InvalidOperationException("VtexPublicClient no tiene IHttpClientFactory. Usa las sobrecargas que reciben HttpClient o construye con factory.");
        return _httpFactory.CreateClient(name);
    }

    private static string NormalizeCountry(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "AR";
        code = code.Trim().ToUpperInvariant();
        return code is "ARG" ? "AR" : code;
    }

    // -------------------------------------------------------
    //  DTOs livianos para no arrastrar modelos gigantes
    // -------------------------------------------------------
    public sealed record PickupPointDto(string Id, string? FriendlyName, string? Address, double? Lon, double? Lat)
    {
        public double[]? GeoCoordinates => Lon.HasValue && Lat.HasValue ? new[] { Lon.Value, Lat.Value } : null;
    }
    public sealed record SkuIdentifier(string Id, string Seller, int Quantity = 1);
    public sealed record MultiSimResult(string Raw, Dictionary<string, SimResultItem> Items);
    public sealed record SimResultItem(bool Available, decimal? Price, decimal? ListPrice);


    public sealed record SellerDto(string Id, string? Name);

    public sealed record SimResult(bool Available, decimal? Price, decimal? ListPrice, string Currency, string Raw);

    // =======================================================
    //  PICKUP-POINTS (GEO)
    // =======================================================
    public async Task<List<PickupPointDto>> GetPickupPointsByGeoAsync(HttpClient http, string host, double lon, double lat, int sc, CancellationToken ct = default)
    {
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/pickup-points?geoCoordinates={lon.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)};{lat.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}&sc={sc}";

        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new List<PickupPointDto>(); // Ser tolerante

            var json = await resp.Content.ReadAsStringAsync(ct);
            var list = new List<PickupPointDto>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var v) ? v.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var friendly = el.TryGetProperty("friendlyName", out var fn) ? fn.GetString() : null;
                    var address = el.TryGetProperty("address", out var ad) ? ad.GetString() : null;

                    double? plat = null, plon = null;
                    if (el.TryGetProperty("geoCoordinates", out var geo) && geo.ValueKind == JsonValueKind.Array && geo.GetArrayLength() == 2)
                    {
                        plon = geo[0].GetDouble();
                        plat = geo[1].GetDouble();
                    }
                    list.Add(new PickupPointDto(id!, friendly, address, plon, plat));
                }
            }
            return list;
        }
        catch
        {
            return new List<PickupPointDto>(); // Ser tolerante a errores de parseo o de red
        }
    }

    public Task<List<PickupPointDto>> GetPickupPointsByGeoAsync(double lon, double lat, int? sc = null, CancellationToken ct = default)
    {
        if (_host is null) throw new InvalidOperationException("Este método requiere que el cliente tenga Host configurado.");
        return GetPickupPointsByGeoAsync(GetClient(), _host, lon, lat, sc ?? _defaultSc, ct);
    }

    // =======================================================
    //  PICKUP-POINTS (POSTAL)
    // =======================================================
    public async Task<List<PickupPointDto>> GetPickupPointsByPostalAsync(HttpClient http, string host, string postalCode, string countryCode, int sc, CancellationToken ct = default)
    {
        var cc = NormalizeCountry(countryCode);
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/pickup-points?postalCode={Uri.EscapeDataString(postalCode)}&countryCode={cc}&sc={sc}";

        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new List<PickupPointDto>();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var list = new List<PickupPointDto>();

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
                return list;
            }

            if (itemsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in itemsArray.EnumerateArray())
                {
                    ParsePickupEl(el, list);
                }
            }
            return list;
        }
        catch
        {
            return new List<PickupPointDto>();
        }

        static void ParsePickupEl(JsonElement el, List<PickupPointDto> list)
        {
            var id = el.TryGetProperty("id", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) return;
            var friendly = el.TryGetProperty("friendlyName", out var fn) ? fn.GetString() : null;
            var addressStr = el.TryGetProperty("address", out var ad) && ad.TryGetProperty("street", out var st) ? st.GetString() : null;

            double? plat = null, plon = null;
            if (el.TryGetProperty("geoCoordinates", out var geo) && geo.ValueKind == JsonValueKind.Array && geo.GetArrayLength() == 2)
            {
                plon = geo[0].GetDouble();
                plat = geo[1].GetDouble();
            }
            list.Add(new PickupPointDto(id!, friendly, addressStr, plon, plat));
        }
    }
    /// <summary>
    /// Sobrecarga de SimulatePickupAsync que NO requiere coordenadas geográficas.
    /// </summary>
    public Task<SimResult> SimulatePickupAsync(
        HttpClient http,
        string host,
        int salesChannel,
        string skuId,
        int quantity,
        string sellerId,
        string countryCode,
        string postalCode, // <-- Nota: aquí no es nullable porque tu servicio siempre lo pasa.
        string pickupPointId,
        CancellationToken ct = default)
    {
        // Este nuevo método es un 'atajo' conveniente.
        // Simplemente llama a la versión más completa pasando 'null' en el parámetro 'geo'.
        return SimulatePickupAsync(http, host, salesChannel, skuId, quantity, sellerId, countryCode, postalCode, null, pickupPointId, ct);
    }

    // Sobrecarga "stateful" SIN GEO
    public Task<SimResult> SimulatePickupAsync(
        int salesChannel,
        string skuId,
        int quantity,
        string sellerId,
        string countryCode,
        string postalCode,
        string pickupPointId,
        CancellationToken ct = default)
    {
        if (_host is null) throw new InvalidOperationException("Este método requiere que el cliente tenga Host configurado.");
        // Llama a la versión completa pasando 'null' en el parámetro 'geo'.
        return SimulatePickupAsync(GetClient(), _host, salesChannel, skuId, quantity, sellerId, countryCode, postalCode, null, pickupPointId, ct);
    }
    public Task<List<PickupPointDto>> GetPickupPointsByPostalAsync(string postalCode, string countryCode, int? sc = null, CancellationToken ct = default)
    {
        if (_host is null) throw new InvalidOperationException("Este método requiere que el cliente tenga Host configurado.");
        return GetPickupPointsByPostalAsync(GetClient(), _host, postalCode, countryCode, sc ?? _defaultSc, ct);
    }

    // =======================================================
    //  REGIONS (sellers por CP)
    // =======================================================
    public async Task<List<SellerDto>> GetRegionSellersByPostalAsync(HttpClient http, string host, string postalCode, string countryCode, int sc, CancellationToken ct = default)
    {
        var cc = NormalizeCountry(countryCode);
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/regions?country={cc}&postalCode={Uri.EscapeDataString(postalCode)}&sc={sc}";

        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new List<SellerDto>();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var sellers = new List<SellerDto>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var regionElement in doc.RootElement.EnumerateArray())
                {
                    if (regionElement.TryGetProperty("sellers", out var sellersArray) && sellersArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sellersArray.EnumerateArray())
                        {
                            var sid = s.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                            var sname = s.TryGetProperty("name", out var n) ? n.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(sid)) sellers.Add(new SellerDto(sid!, sname));
                        }
                    }
                }
            }
            return sellers.DistinctBy(s => s.Id).ToList();
        }
        catch
        {
            return new List<SellerDto>();
        }
    }

    public Task<List<SellerDto>> GetRegionSellersByPostalAsync(string postalCode, string countryCode, int? sc = null, CancellationToken ct = default)
    {
        if (_host is null) throw new InvalidOperationException("Este método requiere que el cliente tenga Host configurado.");
        return GetRegionSellersByPostalAsync(GetClient(), _host, postalCode, countryCode, sc ?? _defaultSc, ct);
    }

    // =======================================================
    //  SIMULACIÓN DE DELIVERY (sin pickup point)
    // =======================================================
    public async Task<SimResult> SimulateDeliveryAsync(
        HttpClient http,
        string host,
        int salesChannel,
        string skuId,
        int quantity,
        string sellerId,
        string countryCode,
        string postalCode,
        CancellationToken ct = default)
    {
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";
        var cc = NormalizeCountry(countryCode);

        var body = new
        {
            country = cc,
            postalCode = postalCode,
            items = new[] { new { id = skuId, quantity = quantity, seller = sellerId } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json")
        };

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new VtexHttpException("Delivery simulation failed", resp.StatusCode, raw, new { skuId, postalCode, sellerId });
        }

        using var doc = JsonDocument.Parse(raw);

        bool available = false;
        if (doc.RootElement.TryGetProperty("logisticsInfo", out var li) && li.ValueKind == JsonValueKind.Array)
        {
            foreach (var logisticsItem in li.EnumerateArray())
            {
                if (logisticsItem.TryGetProperty("slas", out var slas) && slas.ValueKind == JsonValueKind.Array)
                {
                    if (slas.EnumerateArray().Any(sla => sla.TryGetProperty("deliveryChannel", out var dc) && dc.GetString() == "delivery"))
                    {
                        available = true;
                        break;
                    }
                }
            }
        }

        decimal? price = null;
        decimal? listPrice = null;
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            var item = items[0];
            if (item.TryGetProperty("sellingPrice", out var sp) && sp.TryGetDecimal(out var spDecimal)) price = spDecimal / 100m;
            if (item.TryGetProperty("listPrice", out var lp) && lp.TryGetDecimal(out var lpDecimal)) listPrice = lpDecimal / 100m;
        }

        string currency = "ARS";
        if (doc.RootElement.TryGetProperty("storePreferencesData", out var prefs) && prefs.TryGetProperty("currencyCode", out var cc2))
        {
            currency = cc2.GetString() ?? currency;
        }

        return new SimResult(available, price, listPrice, currency, raw);
    }

    public Task<SimResult> SimulateDeliveryAsync(int salesChannel, string skuId, int quantity, string sellerId, string countryCode, string postalCode, CancellationToken ct = default)
    {
        if (_host is null) throw new InvalidOperationException("Este método requiere que el cliente tenga Host configurado.");
        return SimulateDeliveryAsync(GetClient(), _host, salesChannel, skuId, quantity, sellerId, countryCode, postalCode, ct);
    }

    // =======================================================
    //  SIMULACIÓN (pickup-in-point)
    // =======================================================
    public async Task<SimResult> SimulatePickupAsync(
        HttpClient http,
        string host,
        int salesChannel,
        string skuId,
        int quantity,
        string sellerId,
        string countryCode,
        string? postalCode,
        (double lon, double lat)? geo,
        string pickupPointId,
        CancellationToken ct = default)
    {
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";
        var cc = NormalizeCountry(countryCode);

        var body = new
        {
            items = new[] { new { id = skuId, quantity = quantity, seller = sellerId } },
            postalCode = postalCode,
            country = cc,
            shippingData = new
            {
                logisticsInfo = new[]
                {
                    new
                    {
                        itemIndex = 0,
                        selectedSla = pickupPointId,
                        selectedDeliveryChannel = "pickup-in-point"
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json")
        };

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new VtexHttpException("Pickup simulation failed", resp.StatusCode, raw, new { skuId, pickupPointId });
        }

        using var doc = JsonDocument.Parse(raw);
        bool available = false;
        if (doc.RootElement.TryGetProperty("logisticsInfo", out var li) && li.ValueKind == JsonValueKind.Array)
        {
            foreach (var logisticsItem in li.EnumerateArray())
            {
                if (logisticsItem.TryGetProperty("slas", out var slas) && slas.ValueKind == JsonValueKind.Array)
                {
                    if (slas.EnumerateArray().Any(sla =>
                        sla.TryGetProperty("id", out var slaId) && slaId.GetString() == pickupPointId &&
                        sla.TryGetProperty("deliveryChannel", out var dc) && dc.GetString() == "pickup-in-point"))
                    {
                        available = true;
                        break;
                    }
                }
            }
        }

        decimal? price = null;
        decimal? listPrice = null;
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            var item = items[0];
            if (item.TryGetProperty("sellingPrice", out var sp) && sp.TryGetDecimal(out var spDecimal)) price = spDecimal / 100m;
            if (item.TryGetProperty("listPrice", out var lp) && lp.TryGetDecimal(out var lpDecimal)) listPrice = lpDecimal / 100m;
        }

        string currency = "ARS";
        if (doc.RootElement.TryGetProperty("storePreferencesData", out var prefs) && prefs.TryGetProperty("currencyCode", out var cc2))
        {
            currency = cc2.GetString() ?? currency;
        }

        return new SimResult(available, price, listPrice, currency, raw);
    }

    public Task<SimResult> SimulatePickupAsync(int salesChannel, string skuId, int quantity, string sellerId, string countryCode, string? postalCode, (double lon, double lat)? geo, string pickupPointId, CancellationToken ct = default)
    {
        if (_host is null) throw new InvalidOperationException("Este método requiere que el cliente tenga Host configurado.");
        return SimulatePickupAsync(GetClient(), _host, salesChannel, skuId, quantity, sellerId, countryCode, postalCode, geo, pickupPointId, ct);
    }

    // Ruta: ScrapeMart/Services/VtexPublicClient.cs
    public async Task<MultiSimResult> SimulateMultiSkuPickupAsync(
        HttpClient http,
        string host,
        int salesChannel,
        IEnumerable<SkuIdentifier> skus,
        string countryCode,
        string? postalCode,
        string pickupPointId,
        CancellationToken ct = default)
    {
        var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";
        var cc = NormalizeCountry(countryCode);

        var itemsPayload = skus.Select((sku, index) => new {
            id = sku.Id,
            quantity = sku.Quantity,
            seller = sku.Seller,
            itemIndex = index
        }).ToList();

        var logisticsInfoPayload = itemsPayload.Select(item => new {
            itemIndex = item.itemIndex,
            selectedSla = pickupPointId,
            selectedDeliveryChannel = "pickup-in-point"
        }).ToList();

        var body = new
        {
            items = itemsPayload.Select(p => new { p.id, p.quantity, p.seller }).ToList(),
            postalCode,
            country = cc,
            shippingData = new { logisticsInfo = logisticsInfoPayload }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json")
        };

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new VtexHttpException("Multi-SKU Pickup simulation failed", resp.StatusCode, raw, new { pickupPointId, itemCount = skus.Count() });
        }

        var results = new Dictionary<string, SimResultItem>();
        using var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("items", out var itemsArray) && itemsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var itemEl in itemsArray.EnumerateArray())
            {
                var skuId = itemEl.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (skuId is null) continue;

                var availability = itemEl.TryGetProperty("availability", out var availProp) && availProp.GetString() == "available";

                // --- ¡LA CORRECCIÓN ESTÁ AQUÍ! ---
                // Primero verificamos que el campo exista y que NO sea null antes de intentar leerlo como número.
                decimal? price = null;
                if (itemEl.TryGetProperty("sellingPrice", out var sp) && sp.ValueKind == JsonValueKind.Number)
                {
                    price = sp.GetDecimal() / 100m;
                }

                decimal? listPrice = null;
                if (itemEl.TryGetProperty("listPrice", out var lp) && lp.ValueKind == JsonValueKind.Number)
                {
                    listPrice = lp.GetDecimal() / 100m;
                }
                // --- FIN DE LA CORRECCIÓN ---

                results[skuId] = new SimResultItem(availability, price, listPrice);
            }
        }

        return new MultiSimResult(raw, results);
    }
}
