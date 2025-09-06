using Microsoft.Data.SqlClient;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrapeMart.Services;

public sealed class VtexProductSweepService
{
    private readonly IHttpClientFactory _http;
    private readonly string _sqlConn;

    public VtexProductSweepService(IHttpClientFactory http, IConfiguration cfg)
    {
        _http = http;
        _sqlConn = cfg.GetConnectionString("Default")!;
    }

    public async Task<SweepResult> SweepAsync(
        string host,
        string? ft,
        int? categoryId,
        int from = 0,
        int to = 99,
        int step = 50,
        int? sc = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host is required", nameof(host));
        if (ft is null && categoryId is null) throw new ArgumentException("Provide ft or categoryId");

        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ScrapeMart/1.0 (+VTEX product sweep)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var totalRequests = 0;
        var totalProducts = 0;
        var lastHttp = 0;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        for (int start = from; start <= to; start += step)
        {
            int end = Math.Min(start + step - 1, to);

            var url = new StringBuilder();
            url.Append(host.TrimEnd('/'));
            url.Append("/api/catalog_system/pub/products/search?");
            if (categoryId is not null)
                url.Append("fq=C:").Append(categoryId.Value).Append('&');
            else
                url.Append("ft=").Append(Uri.EscapeDataString(ft!)).Append('&');

            url.Append("_from=").Append(start).Append("&_to=").Append(end);
            if (sc is not null) url.Append("&sc=").Append(sc.Value);

            using var resp = await http.GetAsync(url.ToString(), ct);
            lastHttp = (int)resp.StatusCode;
            var json = await resp.Content.ReadAsStringAsync(ct);
            totalRequests++;

            await InsertDiscoveryAsync(host, categoryId is null ? "ft" : "category", categoryId?.ToString() ?? ft!, start, end, lastHttp, json, ct);

            if (!resp.IsSuccessStatusCode) continue;

            VtexProduct[]? products = null;
            try
            {
                products = JsonSerializer.Deserialize<VtexProduct[]>(json, options);
            }
            catch
            {
                // keep going; discovery already stored
            }
            if (products is null || products.Length == 0) break;

            foreach (var p in products)
            {
                await UpsertProductAsync(host, p, ct);
                if (p.Items is null) continue;
                foreach (var it in p.Items)
                {
                    await UpsertSkuAsync(host, p, it, ct);

                    if (it.Images is { Count: > 0 })
                    {
                        foreach (var img in it.Images)
                            await UpsertSkuImageAsync(host, it, img, ct);
                    }

                    if (it.Sellers is { Count: > 0 })
                    {
                        foreach (var s in it.Sellers)
                        {
                            await UpsertSkuSellerAsync(host, it, s, ct);
                            if (s.CommertialOffer is not null)
                                await InsertOfferAsync(host, it, s, sc, ct);
                        }
                    }
                }
            }

            totalProducts += products.Length;
            if (products.Length < step) break; // VTEX usually returns <= page size at end
        }

        return new SweepResult
        {
            Host = host,
            TotalRequests = totalRequests,
            TotalProductsParsed = totalProducts,
            LastHttpStatus = lastHttp
        };
    }

    private async Task InsertDiscoveryAsync(string host, string queryType, string queryValue, int from, int to, int httpStatus, string rawJson, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.VtexCatalogDiscovery
    (RetailerHost, QueryType, QueryValue, RangeFrom, RangeTo, HttpStatus, ItemsFound, RawJson, CapturedAtUtc)
VALUES
    (@host, @type, @value, @from, @to, @status, NULL, @raw, SYSUTCDATETIME());";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@type", queryType);
        cmd.Parameters.AddWithValue("@value", queryValue);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        cmd.Parameters.AddWithValue("@status", httpStatus);
        cmd.Parameters.AddWithValue("@raw", (object?)Truncate(rawJson, 8000) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertProductAsync(string host, VtexProduct p, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexProducts AS T
USING (SELECT @host AS RetailerHost, @pid AS ProductId) AS S
ON (T.RetailerHost=S.RetailerHost AND T.ProductId=S.ProductId)
WHEN MATCHED THEN UPDATE SET
    ProductName=@name,
    Brand=@brand,
    LinkText=@link,
    Categories=@cats,
    LastSeenUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (RetailerHost, ProductId, ProductName, Brand, LinkText, Categories, FirstSeenUtc, LastSeenUtc)
VALUES
    (@host, @pid, @name, @brand, @link, @cats, SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@pid", SafeInt(p.ProductId));
        cmd.Parameters.AddWithValue("@name", (object?)p.ProductName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@brand", (object?)p.Brand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@link", (object?)p.LinkText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cats", (object?)(p.Categories is null ? null : string.Join(" | ", p.Categories)) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertSkuAsync(string host, VtexProduct p, VtexItem it, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexSkus AS T
USING (SELECT @host AS RetailerHost, @sid AS SkuId) AS S
ON (T.RetailerHost=S.RetailerHost AND T.SkuId=S.SkuId)
WHEN MATCHED THEN UPDATE SET
    ProductId=@pid,
    SkuName=@name,
    Ean=@ean,
    RefId=@ref,
    MeasurementUnit=@mu,
    UnitMultiplier=@um,
    LastSeenUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (RetailerHost, SkuId, ProductId, SkuName, Ean, RefId, MeasurementUnit, UnitMultiplier, FirstSeenUtc, LastSeenUtc)
VALUES
    (@host, @sid, @pid, @name, @ean, @ref, @mu, @um, SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@sid", SafeInt(it.ItemId));
        cmd.Parameters.AddWithValue("@pid", SafeInt(p.ProductId));
        cmd.Parameters.AddWithValue("@name", (object?)it.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ean", (object?)it.Ean ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ref", (object?)it.ReferenceId?.FirstOrDefault()?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mu", (object?)it.MeasurementUnit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@um", it.UnitMultiplier.HasValue ? it.UnitMultiplier.Value : 1m);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertSkuImageAsync(string host, VtexItem it, VtexImage img, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexSkuImages AS T
USING (SELECT @host AS RetailerHost, @sid AS SkuId, @imgId AS ImageId) AS S
ON (T.RetailerHost=S.RetailerHost AND T.SkuId=S.SkuId AND T.ImageId=S.ImageId)
WHEN MATCHED THEN UPDATE SET
    Url=@url, ImageLabel=@label, ImageText=@text, LastSeenUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (RetailerHost, SkuId, ImageId, Url, ImageLabel, ImageText, FirstSeenUtc, LastSeenUtc)
VALUES
    (@host, @sid, @imgId, @url, @label, @text, SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@sid", SafeInt(it.ItemId));
        cmd.Parameters.AddWithValue("@imgId", (object?)img.ImageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", (object?)img.ImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@label", (object?)img.ImageLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@text", (object?)img.ImageText ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertSkuSellerAsync(string host, VtexItem it, VtexSeller s, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexSkuSellers AS T
USING (SELECT @host AS RetailerHost, @sid AS SkuId, @seller AS SellerId) AS S
ON (T.RetailerHost=S.RetailerHost AND T.SkuId=S.SkuId AND T.SellerId=S.SellerId)
WHEN MATCHED THEN UPDATE SET
    SellerName=@name,
    SellerDefault=@def,
    LastSeenUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (RetailerHost, SkuId, SellerId, SellerName, SellerDefault, FirstSeenUtc, LastSeenUtc)
VALUES
    (@host, @sid, @seller, @name, @def, SYSUTCDATETIME(), SYSUTCDATETIME());";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@sid", SafeInt(it.ItemId));
        cmd.Parameters.AddWithValue("@seller", (object?)s.SellerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", (object?)s.SellerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@def", s.SellerDefault);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertOfferAsync(string host, VtexItem it, VtexSeller s, int? sc, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.VtexOffers
    (RetailerHost, SkuId, SellerId, SalesChannel, Price, ListPrice, PriceWithoutDiscount, AvailableQuantity, IsAvailable, PriceValidUntilUtc, CapturedAtUtc)
VALUES
    (@host, @sid, @seller, @sc, @price, @list, @pwd, @qty, @avail, @valid, SYSUTCDATETIME());";

        var o = s.CommertialOffer!;
        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@sid", SafeInt(it.ItemId));
        cmd.Parameters.AddWithValue("@seller", (object?)s.SellerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sc", (object?)sc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@price", o.Price ?? 0m);
        cmd.Parameters.AddWithValue("@list", o.ListPrice ?? 0m);
        cmd.Parameters.AddWithValue("@pwd", (object?)o.PriceWithoutDiscount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qty", o.AvailableQuantity ?? 0);
        cmd.Parameters.AddWithValue("@avail", o.IsAvailable ?? false);
        cmd.Parameters.AddWithValue("@valid", (object?)(o.PriceValidUntil ?? (DateTimeOffset?)null)?.UtcDateTime ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static int SafeInt(string? s) =>
        int.TryParse(s, out var i) ? i : 0;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public sealed class SweepResult
    {
        public string Host { get; set; } = default!;
        public int TotalRequests { get; set; }
        public int TotalProductsParsed { get; set; }
        public int LastHttpStatus { get; set; }
    }

    #region POCOs

    public sealed class VtexProduct
    {
        public string? ProductId { get; set; }
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public string[]? Categories { get; set; }
        public string? LinkText { get; set; }
        public List<VtexItem>? Items { get; set; }
    }

    public sealed class VtexItem
    {
        public string? ItemId { get; set; }
        public string? Name { get; set; }
        public string? Ean { get; set; }
        public List<ReferenceId>? ReferenceId { get; set; }
        public string? MeasurementUnit { get; set; }
        public decimal? UnitMultiplier { get; set; }
        public List<VtexImage>? Images { get; set; }
        public List<VtexSeller>? Sellers { get; set; }
    }

    public sealed class ReferenceId
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
    }

    public sealed class VtexImage
    {
        public string? ImageId { get; set; }
        public string? ImageLabel { get; set; }
        public string? ImageUrl { get; set; }
        public string? ImageText { get; set; }
    }

    public sealed class VtexSeller
    {
        public string? SellerId { get; set; }
        public string? SellerName { get; set; }
        public bool SellerDefault { get; set; }
        public CommertialOffer? CommertialOffer { get; set; }
    }

    public sealed class CommertialOffer
    {
        public decimal? Price { get; set; }
        public decimal? ListPrice { get; set; }
        public decimal? PriceWithoutDiscount { get; set; }
        public int? AvailableQuantity { get; set; }
        public bool? IsAvailable { get; set; }
        public DateTimeOffset? PriceValidUntil { get; set; }
    }

    #endregion
}

