// File: Services/VtexSimulationService.cs
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Text;
using System.Text.Json;

namespace ScrapeMart.Services;

/// <summary>
/// Enfoque directo: usar simulation endpoint en lugar del flujo completo de orderForm
/// </summary>
public sealed class VtexSimulationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VtexSimulationService> _log;
    private readonly string _sqlConn;

    public VtexSimulationService(IServiceProvider serviceProvider, ILogger<VtexSimulationService> log, IConfiguration cfg)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
    }

    /// <summary>
    /// Usar simulación directa en lugar del flujo completo
    /// </summary>
    public async Task ProbeAvailabilityWithSimulationAsync(string host, CancellationToken ct)
    {
        _log.LogInformation("🎯 Iniciando prueba DIRECTA de simulación para {Host}", host);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var proxyService = scope.ServiceProvider.GetRequiredService<VtexProxyService>();

        // Obtener configuración
        var config = await db.VtexRetailersConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RetailerHost == host && c.Enabled, ct);

        if (config == null)
        {
            _log.LogError("No se encontró configuración para {Host}", host);
            return;
        }

        var salesChannel = int.Parse(config.SalesChannels.Split(',').First());

        // Obtener productos a testear (limitado)
        var targetEans = await db.ProductsToTrack.AsNoTracking()
            .Select(p => p.EAN)
            .ToListAsync(ct);

        var availableSkus = await db.Sellers.AsNoTracking()
            .Where(s => s.Sku.RetailerHost == host &&
                       s.Sku.Ean != null &&
                       targetEans.Contains(s.Sku.Ean))
            .Select(s => new { s.Sku.ItemId, s.SellerId, s.Sku.Ean, ProductName = s.Sku.Product.ProductName ?? "Sin nombre" })
            .Distinct()
            .Take(5) // Solo 5 para testear
            .ToListAsync(ct);

        // Obtener sucursales
        var storeLocations = await db.VtexPickupPoints.AsNoTracking()
            .Where(pp => pp.RetailerHost == host && pp.SourceIdSucursal.HasValue)
            .Join(db.Sucursales.AsNoTracking(),
                  pp => new { B = pp.SourceIdBandera!.Value, C = pp.SourceIdComercio!.Value, S = pp.SourceIdSucursal.Value },
                  s => new { B = s.IdBandera, C = s.IdComercio, S = s.IdSucursal },
                  (pp, s) => new { pp.PickupPointId, s.SucursalesCodigoPostal, s.SucursalesNombre })
            .Take(3) // Solo 3 sucursales para testear
            .ToListAsync(ct);

        _log.LogInformation("Testing: {SkuCount} SKUs × {StoreCount} stores", availableSkus.Count, storeLocations.Count);

        using var httpClient = proxyService.CreateDirectClient(); // Usar conexión directa que sabemos que funciona

        foreach (var sku in availableSkus)
        {
            foreach (var store in storeLocations)
            {
                _log.LogInformation("🧪 Testing simulación: {Product} en {Store}", sku.ProductName, store.SucursalesNombre);

                try
                {
                    var result = await SimulatePickupDirectlyAsync(httpClient, host, salesChannel, sku.ItemId, sku.SellerId, store.PickupPointId, store.SucursalesCodigoPostal, ct);

                    // Guardar resultado
                    await SaveSimulationResultAsync(host, sku.ItemId, sku.SellerId, store.PickupPointId, salesChannel, result, ct);

                    if (result.IsAvailable)
                    {
                        _log.LogInformation("✅ {Product} DISPONIBLE en {Store} - ${Price:F2}", sku.ProductName, store.SucursalesNombre, result.Price);
                    }
                    else
                    {
                        _log.LogInformation("❌ {Product} NO disponible en {Store} - {Error}", sku.ProductName, store.SucursalesNombre, result.Error);
                    }

                    await Task.Delay(500, ct); // Pausa entre requests
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error simulando {Product} en {Store}", sku.ProductName, store.SucursalesNombre);
                }
            }
        }

        _log.LogInformation("Simulación completada para {Host}", host);
    }

    /// <summary>
    /// Simulación directa usando endpoint de simulation
    /// </summary>
    private async Task<SimulationResult> SimulatePickupDirectlyAsync(
        HttpClient httpClient,
        string host,
        int salesChannel,
        string skuId,
        string sellerId,
        string pickupPointId,
        string postalCode,
        CancellationToken ct)
    {
        var result = new SimulationResult();

        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";

            var payload = new
            {
                items = new[]
                {
                    new
                    {
                        id = skuId,
                        quantity = 1,
                        seller = sellerId
                    }
                },
                postalCode = postalCode,
                country = "AR",
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

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            request.Headers.Add("Referer", host + "/");
            request.Headers.Add("x-requested-with", "XMLHttpRequest");

            using var response = await httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            result.RawResponse = responseBody;

            if (!response.IsSuccessStatusCode)
            {
                result.Error = $"HTTP {response.StatusCode}";

                if (responseBody.Contains("CHK003"))
                {
                    result.Error = "Bloqueado CHK003";
                }
                else if (responseBody.Contains("ORD002"))
                {
                    result.Error = "Carrito inválido";
                }

                return result;
            }

            // Parsear respuesta de simulación
            using var doc = JsonDocument.Parse(responseBody);

            // Verificar items
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                var item = items[0];

                if (item.TryGetProperty("availability", out var avail) && avail.GetString() == "available")
                {
                    result.IsAvailable = true;

                    if (item.TryGetProperty("sellingPrice", out var sp) && sp.TryGetDecimal(out var price))
                        result.Price = price / 100m;

                    if (item.TryGetProperty("listPrice", out var lp) && lp.TryGetDecimal(out var listPrice))
                        result.ListPrice = listPrice / 100m;
                }
                else
                {
                    result.Error = "Item no disponible";
                }
            }
            else
            {
                result.Error = "Sin items en respuesta";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    private async Task SaveSimulationResultAsync(string host, string skuId, string sellerId, string pickupPointId, int salesChannel, SimulationResult result, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.VtexStoreAvailability AS T
USING (VALUES(@host,@pp,@sku,@seller,@sc)) AS S (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel)
ON (T.RetailerHost=S.RetailerHost AND T.PickupPointId=S.PickupPointId AND T.SkuId=S.SkuId AND T.SellerId=S.SellerId AND T.SalesChannel=S.SalesChannel)
WHEN MATCHED THEN
  UPDATE SET IsAvailable=@avail, MaxFeasibleQty=@maxQty, Price=@price, Currency=@curr, CountryCode=@country, PostalCode=@postal, CapturedAtUtc=SYSUTCDATETIME(), RawJson=@raw, ErrorMessage=@error
WHEN NOT MATCHED THEN
  INSERT (RetailerHost,PickupPointId,SkuId,SellerId,SalesChannel,CountryCode,PostalCode,IsAvailable,MaxFeasibleQty,Price,Currency,CapturedAtUtc,RawJson,ErrorMessage)
  VALUES (@host,@pp,@sku,@seller,@sc,@country,@postal,@avail,@maxQty,@price,@curr,SYSUTCDATETIME(),@raw,@error);";

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);

        cmd.Parameters.AddWithValue("@host", host);
        cmd.Parameters.AddWithValue("@pp", pickupPointId);
        cmd.Parameters.AddWithValue("@sku", skuId);
        cmd.Parameters.AddWithValue("@seller", sellerId);
        cmd.Parameters.AddWithValue("@sc", salesChannel);
        cmd.Parameters.AddWithValue("@country", "AR");
        cmd.Parameters.AddWithValue("@postal", "");
        cmd.Parameters.AddWithValue("@avail", result.IsAvailable);
        cmd.Parameters.AddWithValue("@maxQty", result.IsAvailable ? 1 : 0);
        cmd.Parameters.AddWithValue("@price", (object?)result.Price ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@curr", "ARS");
        cmd.Parameters.AddWithValue("@raw", (object?)result.RawResponse?.Substring(0, Math.Min(result.RawResponse.Length, 4000)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)result.Error ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class SimulationResult
    {
        public bool IsAvailable { get; set; }
        public decimal? Price { get; set; }
        public decimal? ListPrice { get; set; }
        public string? Error { get; set; }
        public string? RawResponse { get; set; }
    }
}