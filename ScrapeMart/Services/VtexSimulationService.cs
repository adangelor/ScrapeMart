// File: Services/VtexSimulationService.cs - VERSIÓN NUCLEAR: TODOS los productos vs TODAS las cadenas
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using System.Text;
using System.Text.Json;
using System.Net;

namespace ScrapeMart.Services;

/// <summary>
/// 🚀 VERSIÓN NUCLEAR: Testea TODOS los productos de ProductsToTrack en TODAS las cadenas VTEX
/// </summary>
public sealed class VtexSimulationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VtexSimulationService> _log;
    private readonly string _sqlConn;
    private readonly IVtexCookieManager _cookieManager;

    public VtexSimulationService(
        IServiceProvider serviceProvider,
        ILogger<VtexSimulationService> log,
        IConfiguration cfg,
        IVtexCookieManager cookieManager)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _sqlConn = cfg.GetConnectionString("Default")!;
        _cookieManager = cookieManager;
    }

    /// <summary>
    /// 🚀 MÉTODO NUCLEAR: Todos los productos vs todas las cadenas
    /// </summary>
    public async Task ProbeAvailabilityWithSimulationAsync(string? specificHost = null, CancellationToken ct = default)
    {
        _log.LogInformation("🚀 INICIANDO BOMBARDEO NUCLEAR: TODOS los productos vs TODAS las cadenas VTEX");

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        // 🏢 OBTENER TODAS LAS CADENAS HABILITADAS (o una específica)
        var retailers = await db.VtexRetailersConfigs
            .Where(r => r.Enabled && (specificHost == null || r.RetailerHost == specificHost))
            .AsNoTracking()
            .ToListAsync(ct);

        _log.LogInformation("🏢 Cadenas a procesar: {Count}", retailers.Count);
        foreach (var retailer in retailers)
        {
            _log.LogInformation("  - {Host} (SC: {SalesChannels})", retailer.RetailerHost, retailer.SalesChannels);
        }

        // 📋 OBTENER TODOS LOS PRODUCTOS A TRACKEAR
        var allProductsToTrack = await db.ProductsToTrack
            .AsNoTracking()
            .Select(p => new ProductToTest
            {
                EAN = p.EAN,
                Owner = p.Owner,
                ProductName = p.ProductName ?? "Sin nombre"
            })
            .ToListAsync(ct);

        _log.LogInformation("📋 Productos a trackear: {Count} ({AdecoCount} Adeco + {CompetitorCount} competencia)",
            allProductsToTrack.Count,
            allProductsToTrack.Count(p => p.Owner == "Adeco"),
            allProductsToTrack.Count(p => p.Owner != "Adeco"));

        // 🎯 ESTADÍSTICAS TOTALES
        var totalCombinations = 0;
        var successfulTests = 0;
        var failedTests = 0;

        // 🚀 PROCESAR CADA CADENA
        foreach (var retailer in retailers)
        {
            if (ct.IsCancellationRequested) break;

            _log.LogInformation("🏢 === PROCESANDO {Host} ===", retailer.RetailerHost);

            try
            {
                var retailerResult = await ProcessRetailerAsync(retailer, allProductsToTrack, ct);

                totalCombinations += retailerResult.TotalTests;
                successfulTests += retailerResult.SuccessfulTests;
                failedTests += retailerResult.FailedTests;

                _log.LogInformation("✅ {Host} completado: {Success}/{Total} exitosos",
                    retailer.RetailerHost, retailerResult.SuccessfulTests, retailerResult.TotalTests);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "💥 Error procesando cadena {Host}", retailer.RetailerHost);
                failedTests += allProductsToTrack.Count; // Asumir que todos fallaron
            }

            // Pausa entre cadenas
            await Task.Delay(2000, ct);
        }

        // 📊 REPORTE FINAL
        _log.LogInformation("🎉 === BOMBARDEO NUCLEAR COMPLETADO ===");
        _log.LogInformation("📊 ESTADÍSTICAS FINALES:");
        _log.LogInformation("  🏢 Cadenas procesadas: {Retailers}", retailers.Count);
        _log.LogInformation("  📋 Productos testeados: {Products}", allProductsToTrack.Count);
        _log.LogInformation("  🎯 Total combinaciones: {Total}", totalCombinations);
        _log.LogInformation("  ✅ Tests exitosos: {Success}", successfulTests);
        _log.LogInformation("  ❌ Tests fallidos: {Failed}", failedTests);
        _log.LogInformation("  📈 Tasa de éxito: {SuccessRate:P2}",
            totalCombinations > 0 ? (double)successfulTests / totalCombinations : 0);
    }

    /// <summary>
    /// 🏢 Procesar una cadena específica contra todos los productos
    /// </summary>
    private async Task<RetailerTestResult> ProcessRetailerAsync(
        Entities.VtexRetailersConfig retailer,
        List<ProductToTest> allProducts,
        CancellationToken ct)
    {
        var result = new RetailerTestResult();
        var salesChannel = int.Parse(retailer.SalesChannels.Split(',').First());

        // 🍪 CONFIGURAR COOKIES PARA ESTA CADENA
        await SetupCookiesForRetailer(retailer.RetailerHost, salesChannel);

        // 🍪 CREAR CLIENTE CON COOKIES DEL MANAGER
        using var httpClient = CreateClientWithCookieManager(retailer.RetailerHost);

        // 🔍 OBTENER SKUS DISPONIBLES EN ESTA CADENA PARA LOS PRODUCTOS TRACKEADOS
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        var targetEans = allProducts.Select(p => p.EAN).ToList();

        var availableSkus = await db.Sellers
            .Where(s => s.Sku.RetailerHost == retailer.RetailerHost &&
                       s.Sku.Ean != null &&
                       targetEans.Contains(s.Sku.Ean))
            .Select(s => new SkuToTest
            {
                SkuId = s.Sku.ItemId,
                SellerId = s.SellerId,
                Ean = s.Sku.Ean!,
                ProductName = allProducts.First(p => p.EAN == s.Sku.Ean).ProductName,
                Owner = allProducts.First(p => p.EAN == s.Sku.Ean).Owner
            })
            .Distinct()
            .AsNoTracking()
            .ToListAsync(ct);

        _log.LogInformation("🔍 {Host}: {SkusFound}/{TotalProducts} productos encontrados en catálogo",
            retailer.RetailerHost, availableSkus.Count, allProducts.Count);

        if (availableSkus.Count == 0)
        {
            _log.LogWarning("⚠️ {Host}: No se encontraron SKUs, saltando cadena", retailer.RetailerHost);
            return result;
        }

        // 🎯 CONFIGURACIÓN DE PROCESAMIENTO PARALELO
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 6, // Ajusta según tu capacidad
            CancellationToken = ct
        };

        // 🚀 PROCESAR SKUS EN PARALELO
        var results = new List<TestResult>();
        var lockObject = new object();

        await Parallel.ForEachAsync(availableSkus, parallelOptions, async (sku, token) =>
        {
            if (token.IsCancellationRequested) return;

            try
            {
                var testResult = await TestSingleSkuAsync(httpClient, retailer.RetailerHost, salesChannel, sku, token);

                lock (lockObject)
                {
                    results.Add(testResult);
                    if (testResult.Success) result.SuccessfulTests++;
                    result.TotalTests++;
                }

                // Log progreso cada 10 tests
                if (result.TotalTests % 10 == 0)
                {
                    _log.LogInformation("📊 {Host}: {Completed}/{Total} tests completados",
                        retailer.RetailerHost, result.TotalTests, availableSkus.Count);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "💥 Error testeando {Product} en {Host}", sku.ProductName, retailer.RetailerHost);
                lock (lockObject)
                {
                    result.TotalTests++;
                }
            }
        });

        // 📊 GUARDAR TODOS LOS RESULTADOS EN BATCH
        await SaveBatchResultsAsync(retailer.RetailerHost, salesChannel, results, ct);

        // 📈 REPORTE POR CADENA
        var adecoTests = results.Count(r => r.Sku.Owner == "Adeco");
        var adecoSuccess = results.Count(r => r.Sku.Owner == "Adeco" && r.Success);

        _log.LogInformation("📈 {Host} - RESUMEN:", retailer.RetailerHost);
        _log.LogInformation("  📋 Total productos: {Total}", result.TotalTests);
        _log.LogInformation("  ✅ Disponibles: {Success}", result.SuccessfulTests);
        _log.LogInformation("  🏷️ Productos Adeco: {AdecoSuccess}/{AdecoTotal}", adecoSuccess, adecoTests);

        return result;
    }

    /// <summary>
    /// 🍪 Configurar cookies específicas por cadena
    /// </summary>
    private async Task SetupCookiesForRetailer(string host, int salesChannel)
    {
        _log.LogDebug("🍪 Configurando cookies para {Host} (SC: {SalesChannel})", host, salesChannel);

        // Warmup básico - el VtexCookieManager se encarga del resto
        using var tempClient = CreateClientWithCookieManager(host);
        await _cookieManager.WarmupCookiesAsync(tempClient, host);

        // Actualizar segment cookie con sales channel correcto
        _cookieManager.UpdateSegmentCookie(host, salesChannel);
    }
    /// <summary>
    /// 🍪 Crear HttpClient con cookies del manager
    /// </summary>
    private HttpClient CreateClientWithCookieManager(string host)
    {
        var cookieContainer = _cookieManager.GetCookieContainer(host);
        var handler = new HttpClientHandler()
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9,en;q=0.8");
        client.Timeout = TimeSpan.FromSeconds(30);

        return client;
    }

    /// <summary>
    /// 🧪 Testear un solo SKU
    /// </summary>
    private async Task<TestResult> TestSingleSkuAsync(HttpClient httpClient, string host, int salesChannel, SkuToTest sku, CancellationToken ct)
    {
        var result = new TestResult { Sku = sku };

        try
        {
            var url = $"{host.TrimEnd('/')}/api/checkout/pub/orderForms/simulation?sc={salesChannel}";

            var payload = new
            {
                items = new[] { new { id = sku.SkuId, quantity = 1, seller = sku.SellerId } },
                country = "ARG"
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            request.Headers.Add("Referer", host + "/");
            request.Headers.Add("x-requested-with", "XMLHttpRequest");

            using var response = await httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            result.RawResponse = responseBody;
            result.StatusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var item = items[0];
                    if (item.TryGetProperty("availability", out var avail) && avail.GetString() == "available")
                    {
                        result.Success = true;
                        result.IsAvailable = true;

                        if (item.TryGetProperty("sellingPrice", out var sp) && sp.TryGetDecimal(out var price))
                            result.Price = price / 100m;
                    }
                }
            }
            else
            {
                if (responseBody.Contains("CHK003")) result.ErrorMessage = "CHK003 - Bloqueado";
                else if (responseBody.Contains("CHK002")) result.ErrorMessage = "CHK002 - Request inválido";
                else result.ErrorMessage = $"HTTP {response.StatusCode}";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 💾 Guardar resultados en batch (más eficiente)
    /// </summary>
    private async Task SaveBatchResultsAsync(string host, int salesChannel, List<TestResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return;

        try
        {
            await using var connection = new SqlConnection(_sqlConn);
            await connection.OpenAsync(ct);

            using var transaction = connection.BeginTransaction();

            foreach (var result in results)
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

                await using var cmd = new SqlCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("@host", host);
                cmd.Parameters.AddWithValue("@pp", "simulation-test");
                cmd.Parameters.AddWithValue("@sku", result.Sku.SkuId);
                cmd.Parameters.AddWithValue("@seller", result.Sku.SellerId);
                cmd.Parameters.AddWithValue("@sc", salesChannel);
                cmd.Parameters.AddWithValue("@country", "AR");
                cmd.Parameters.AddWithValue("@postal", "");
                cmd.Parameters.AddWithValue("@avail", result.IsAvailable);
                cmd.Parameters.AddWithValue("@maxQty", result.IsAvailable ? 1 : 0);
                cmd.Parameters.AddWithValue("@price", (object?)result.Price ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@curr", "ARS");
                cmd.Parameters.AddWithValue("@raw", (object?)result.RawResponse?.Substring(0, Math.Min(result.RawResponse?.Length ?? 0, 4000)) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@error", (object?)result.ErrorMessage ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync(ct);
            }

            transaction.Commit();
            _log.LogInformation("💾 Guardados {Count} resultados para {Host}", results.Count, host);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "💥 Error guardando batch de {Count} resultados", results.Count);
        }
    }

    #region DTOs
    private sealed class ProductToTest
    {
        public string EAN { get; set; } = default!;
        public string Owner { get; set; } = default!;
        public string ProductName { get; set; } = default!;
    }

    private sealed class SkuToTest
    {
        public string SkuId { get; set; } = default!;
        public string SellerId { get; set; } = default!;
        public string Ean { get; set; } = default!;
        public string ProductName { get; set; } = default!;
        public string Owner { get; set; } = default!;
    }

    private sealed class TestResult
    {
        public SkuToTest Sku { get; set; } = default!;
        public bool Success { get; set; }
        public bool IsAvailable { get; set; }
        public decimal? Price { get; set; }
        public int StatusCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RawResponse { get; set; }
    }

    private sealed class RetailerTestResult
    {
        public int TotalTests { get; set; }
        public int SuccessfulTests { get; set; }
        public int FailedTests => TotalTests - SuccessfulTests;
    }
    #endregion
}