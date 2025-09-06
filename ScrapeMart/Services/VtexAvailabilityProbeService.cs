// File: Services/VtexAvailabilityProbeService.cs
using Microsoft.Data.SqlClient;

namespace ScrapeMart.Services;

public sealed class VtexAvailabilityProbeService
{
    private readonly VtexPublicClient _vtex;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<VtexAvailabilityProbeService> _log;
    private readonly string _sqlConn;

    public VtexAvailabilityProbeService(
        VtexPublicClient vtex,
        IHttpClientFactory http,
        ILogger<VtexAvailabilityProbeService> log,
        IConfiguration cfg)
    {
        _vtex = vtex;
        _http = http;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
    }

    public async Task<ProbeResult> ProbePickupAsync(
        string host,
        int salesChannel,
        string skuId,
        string sellerId,
        string pickupPointId,
        string countryCode,
        string postalCode,
        int maxCap = 512,
        CancellationToken ct = default)
    {
        var http = _http.CreateClient("vtexSession");
        // Paso 0: chequeo con qty=1
        var sim1 = await _vtex.SimulatePickupAsync(http, host, salesChannel, skuId, 1, sellerId, countryCode, postalCode, pickupPointId, ct);

        // --- CORRECCIÓN AQUÍ ---
        if (!sim1.Available)
        {
            // --- Y CORRECCIÓN AQUÍ ---
            await UpsertAsync(host, pickupPointId, skuId, sellerId, salesChannel, countryCode, postalCode,
                              false, 0, sim1.Price, sim1.Currency, sim1.Raw, ct);
            // --- Y CORRECCIÓN AQUÍ ---
            return new ProbeResult(false, 0, sim1.Price, sim1.Currency);
        }

        // Búsqueda exponencial
        int lo = 1, hi = 2;
        while (hi <= maxCap)
        {
            var sim = await _vtex.SimulatePickupAsync(http, host, salesChannel, skuId, hi, sellerId, countryCode, postalCode, pickupPointId, ct);
            // --- CORRECCIÓN AQUÍ ---
            if (sim.Available)
            {
                lo = hi;
                hi *= 2;
            }
            else
            {
                break;
            }
        }
        if (hi > maxCap) hi = maxCap + 1; // para binaria acotada

        // Búsqueda binaria en (lo, hi)
        int best = lo;
        int left = lo + 1, right = Math.Max(lo + 1, hi - 1);
        while (left <= right)
        {
            int mid = (left + right) / 2;
            var sim = await _vtex.SimulatePickupAsync(http, host, salesChannel, skuId, mid, sellerId, countryCode, postalCode, pickupPointId, ct);
            // --- CORRECCIÓN AQUÍ ---
            if (sim.Available)
            {
                best = mid;
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        // Guardar con el precio de sim1
        // --- CORRECCIÓN AQUÍ ---
        await UpsertAsync(host, pickupPointId, skuId, sellerId, salesChannel, countryCode, postalCode,
                          true, best, sim1.Price, sim1.Currency, sim1.Raw, ct);

        // --- Y CORRECCIÓN AQUÍ ---
        return new ProbeResult(true, best, sim1.Price, sim1.Currency);
    }

    private async Task UpsertAsync(
        string host, string pickupPointId, string skuId, string sellerId, int sc,
        string country, string postal, bool available, int maxQty, decimal? price, string currency, string raw, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexStoreAvailability AS T
USING (VALUES(@host,@pp,@sku,@seller,@sc)) AS S (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel)
ON (T.RetailerHost=S.RetailerHost AND T.PickupPointId=S.PickupPointId AND T.SkuId=S.SkuId AND T.SellerId=S.SellerId AND T.SalesChannel=S.SalesChannel)
WHEN MATCHED THEN
  UPDATE SET IsAvailable=@avail, MaxFeasibleQty=@maxQty, Price=@price, Currency=@curr, CountryCode=@country, PostalCode=@postal, CapturedAtUtc=SYSUTCDATETIME(), RawJson=@raw
WHEN NOT MATCHED THEN
  INSERT (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel,CountryCode,PostalCode,IsAvailable,MaxFeasibleQty,Price,Currency,CapturedAtUtc,RawJson)
  VALUES (@host,@pp,@sku,@seller,@sc,@country,@postal,@avail,@maxQty,@price,@curr,SYSUTCDATETIME(),@raw);";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@pp", pickupPointId);
        cmd.Parameters.AddWithValue("@sku", skuId);
        cmd.Parameters.AddWithValue("@seller", sellerId);
        cmd.Parameters.AddWithValue("@sc", sc);
        cmd.Parameters.AddWithValue("@country", (object?)country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@postal", (object?)postal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@avail", available);
        cmd.Parameters.AddWithValue("@maxQty", maxQty);
        cmd.Parameters.AddWithValue("@price", (object?)price ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@curr", (object?)currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@raw", (object?)raw ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public readonly record struct ProbeResult(bool IsAvailable, int MaxFeasibleQty, decimal? Price, string Currency);
}