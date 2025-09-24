using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities;
using ScrapeMart.Storage;

namespace ScrapeMart.Services;

/// <summary>
/// Servicio que implementa tu lógica real:
/// 1. Recorre todas las sucursales de todas las cadenas VTEX
/// 2. Para cada sucursal, verifica disponibilidad de productos de ProductsToTrack
/// 3. Persiste los resultados
/// </summary>
public sealed class ComprehensiveAvailabilityService
{
    private readonly AppDb _db;
    private readonly VtexPublicClient _vtexClient;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ComprehensiveAvailabilityService> _logger;
    private readonly string _connectionString;

    public ComprehensiveAvailabilityService(
        AppDb db,
        VtexPublicClient vtexClient,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<ComprehensiveAvailabilityService> logger)
    {
        _db = db;
        _vtexClient = vtexClient;
        _httpFactory = httpFactory;
        _logger = logger;
        _connectionString = config.GetConnectionString("Default")!;
    }

    /// <summary>
    /// Proceso principal: recorre TODAS las sucursales de TODAS las cadenas VTEX
    /// y verifica disponibilidad de TODOS los productos de ProductsToTrack
    /// </summary>
    public async Task<ComprehensiveAvailabilityResult> RunComprehensiveAvailabilityCheckAsync(
        string? specificRetailerHost = null,
        CancellationToken ct = default)
    {
        var result = new ComprehensiveAvailabilityResult();

        _logger.LogInformation("=== INICIANDO VERIFICACIÓN COMPREHENSIVA DE DISPONIBILIDAD ===");

        try
        {
            // 1. Obtener todas las cadenas VTEX habilitadas
            var retailers = await _db.VtexRetailersConfigs
                .Where(r => r.Enabled && (specificRetailerHost == null || r.RetailerHost == specificRetailerHost))
                .AsNoTracking()
                .ToListAsync(ct);

            _logger.LogInformation("Procesando {Count} cadenas VTEX", retailers.Count);

            // 2. Obtener todos los productos a trackear
            var productsToTrack = await _db.ProductsToTrack
                .AsNoTracking()
                .ToListAsync(ct);

            _logger.LogInformation("Trackeando {Count} productos ({AdecoCount} Adeco + {CompetitorCount} competencia)",
                productsToTrack.Count,
                productsToTrack.Count(p => p.Owner == "Adeco"),
                productsToTrack.Count(p => p.Owner != "Adeco"));

            result.TotalRetailers = retailers.Count;
            result.TotalProductsToTrack = productsToTrack.Count;

            // 3. Para cada cadena VTEX...
            foreach (var retailer in retailers)
            {
                if (ct.IsCancellationRequested) break;

                _logger.LogInformation("--- Procesando cadena: {RetailerHost} ---", retailer.RetailerHost);

                try
                {
                    // 3.1 Obtener sucursales de esta cadena
                    var stores = await GetStoresForRetailerAsync(retailer, ct);
                    _logger.LogInformation("Encontradas {Count} sucursales para {Retailer}", stores.Count, retailer.RetailerHost);

                    if (stores.Count == 0)
                    {
                        _logger.LogWarning("No se encontraron sucursales para {Retailer}", retailer.RetailerHost);
                        continue;
                    }

                    // 3.2 Procesar cada sucursal
                    var retailerResult = await ProcessRetailerStoresAsync(retailer, stores, productsToTrack, ct);

                    result.RetailerResults[retailer.RetailerHost] = retailerResult;
                    result.TotalStoresProcessed += retailerResult.StoresProcessed;
                    result.TotalProductChecks += retailerResult.ProductChecks;
                    result.TotalAvailableProducts += retailerResult.AvailableProducts;

                    _logger.LogInformation("Completado {Retailer}: {Stores} sucursales, {Checks} verificaciones, {Available} disponibles",
                        retailer.RetailerHost, retailerResult.StoresProcessed, retailerResult.ProductChecks, retailerResult.AvailableProducts);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando cadena {Retailer}", retailer.RetailerHost);
                    result.RetailerResults[retailer.RetailerHost] = new RetailerAvailabilityResult
                    {
                        RetailerHost = retailer.RetailerHost,
                        ErrorMessage = ex.Message
                    };
                }

                // Pausa entre cadenas para evitar rate limiting
                await Task.Delay(2000, ct);
            }

            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("=== VERIFICACIÓN COMPREHENSIVA COMPLETADA ===");
            _logger.LogInformation("Resumen: {TotalStores} sucursales, {TotalChecks} verificaciones, {TotalAvailable} productos disponibles",
                result.TotalStoresProcessed, result.TotalProductChecks, result.TotalAvailableProducts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en verificación comprehensiva");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<List<StoreLocation>> GetStoresForRetailerAsync(VtexRetailersConfig retailer, CancellationToken ct)
    {
        // Buscar por IdBandera e IdComercio si están configurados en el retailer
        // Si no, usar lookup por nombre o algún otro criterio

        var query = _db.Sucursales.AsQueryable();

        // Aquí necesitarías ajustar según cómo mapees las cadenas a IdBandera/IdComercio
        // Por ejemplo, podrías tener una tabla de mapeo o usar convenciones de nombres

        var stores = await query
            .Where(s => s.SucursalesLatitud != 0 && s.SucursalesLongitud != 0) // Solo sucursales con coordenadas
            .Select(s => new StoreLocation
            {
                IdBandera = s.IdBandera,
                IdComercio = s.IdComercio,
                IdSucursal = s.IdSucursal,
                Name = s.SucursalesNombre,
                PostalCode = s.SucursalesCodigoPostal,
                Latitude = s.SucursalesLatitud,
                Longitude = s.SucursalesLongitud
            })
            .ToListAsync(ct);

        return stores;
    }

    private async Task<RetailerAvailabilityResult> ProcessRetailerStoresAsync(
        VtexRetailersConfig retailer,
        List<StoreLocation> stores,
        List<ProductToTrack> productsToTrack,
        CancellationToken ct)
    {
        var result = new RetailerAvailabilityResult { RetailerHost = retailer.RetailerHost };
        var http = _httpFactory.CreateClient("vtexSession");

        // Inicialización para esta cadena (siguiendo tu colección Postman)
        var warmupSuccess = await _vtexClient.WarmupHomepageAsync(http, retailer.RetailerHost, ct);
        if (!warmupSuccess)
        {
            result.ErrorMessage = "Falló warmup";
            return result;
        }

        var orderFormResult = await _vtexClient.CreateOrderFormAsync(http, retailer.RetailerHost, ct);
        if (!orderFormResult.Success)
        {
            result.ErrorMessage = "Falló OrderForm creation";
            return result;
        }

        var salesChannels = retailer.SalesChannels.Split(',').Select(int.Parse).ToList();
        var primarySalesChannel = salesChannels.FirstOrDefault();

        // Para cada sucursal de esta cadena...
        foreach (var store in stores.Take(10)) // LIMITADO A 10 para testing - quitar el Take en producción
        {
            if (ct.IsCancellationRequested) break;

            _logger.LogInformation("Procesando sucursal {StoreName} ({IdSucursal})", store.Name, store.IdSucursal);

            // Para cada producto a trackear...
            foreach (var product in productsToTrack)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // 1. Buscar el producto por EAN en esta cadena
                    var productSearch = await _vtexClient.SearchProductByEanAsync(http, retailer.RetailerHost, product.EAN, ct);

                    if (!productSearch.Success || string.IsNullOrEmpty(productSearch.SkuId))
                    {
                        // Producto no encontrado en esta cadena
                        await PersistAvailabilityResultAsync(retailer.RetailerHost, store, product.EAN, null, null, false, 0, 0, 0, ct);
                        continue;
                    }

                    // 2. Simular disponibilidad en esta sucursal
                    var simulation = await _vtexClient.SimulateAtStoreLocationAsync(
                        http, retailer.RetailerHost, productSearch.SkuId, productSearch.SellerId,
                        store.Longitude, store.Latitude, store.PostalCode, // ¡FALTABA ESTO!
                        salesChannel: primarySalesChannel, ct: ct);
                    // 3. Persistir resultado
                    bool isAvailable = simulation.Success && simulation.Availability?.Available == true;
                    int stock = simulation.Availability?.Quantity ?? 0;
                    decimal price = simulation.Availability?.Price ?? 0;
                    decimal listPrice = simulation.Availability?.ListPrice ?? 0;

                    await PersistAvailabilityResultAsync(
                        retailer.RetailerHost, store, product.EAN, productSearch.SkuId,
                        productSearch.SellerId, isAvailable, stock, price, listPrice, ct);

                    result.ProductChecks++;
                    if (isAvailable) result.AvailableProducts++;

                    _logger.LogDebug("Producto {EAN} en {Store}: {Available}",
                        product.EAN, store.Name, isAvailable ? "DISPONIBLE" : "NO DISPONIBLE");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error verificando producto {EAN} en sucursal {Store}",
                        product.EAN, store.Name);
                }

                // Pequeña pausa entre productos
                await Task.Delay(100, ct);
            }

            result.StoresProcessed++;

            // Pausa entre sucursales
            await Task.Delay(500, ct);
        }

        return result;
    }

    private async Task PersistAvailabilityResultAsync(
        string retailerHost,
        StoreLocation store,
        string ean,
        string? skuId,
        string? sellerId,
        bool available,
        int stock,
        decimal price,
        decimal listPrice,
        CancellationToken ct)
    {
        // Aquí persistirías en tu tabla de resultados
        // Podrías usar la misma tabla que ya tienes o crear una nueva específica

        const string sql = @"
            INSERT INTO ProductAvailabilityResults 
            (RetailerHost, IdBandera, IdComercio, IdSucursal, EAN, SkuId, SellerId, 
             Available, Stock, Price, ListPrice, CheckedAt)
            VALUES 
            (@retailerHost, @idBandera, @idComercio, @idSucursal, @ean, @skuId, @sellerId,
             @available, @stock, @price, @listPrice, GETUTCDATE())";

        // Implementar la inserción usando tu connection string...
        // await ExecuteSqlAsync(sql, parameters, ct);
    }

    // DTOs para el servicio
    public sealed class StoreLocation
    {
        public int IdBandera { get; set; }
        public int IdComercio { get; set; }
        public int IdSucursal { get; set; }
        public string Name { get; set; } = "";
        public string PostalCode { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public sealed class ComprehensiveAvailabilityResult
    {
        public bool Success { get; set; }
        public int TotalRetailers { get; set; }
        public int TotalProductsToTrack { get; set; }
        public int TotalStoresProcessed { get; set; }
        public int TotalProductChecks { get; set; }
        public int TotalAvailableProducts { get; set; }
        public Dictionary<string, RetailerAvailabilityResult> RetailerResults { get; set; } = new();
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public sealed class RetailerAvailabilityResult
    {
        public string RetailerHost { get; set; } = "";
        public int StoresProcessed { get; set; }
        public int ProductChecks { get; set; }
        public int AvailableProducts { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
