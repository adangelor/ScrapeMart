//// ScrapeMart/Services/ProximityAvailabilityService.cs

//using Microsoft.EntityFrameworkCore;
//using ScrapeMart.Entities.dtos;
//using ScrapeMart.Storage;
//using System.Net;

//namespace ScrapeMart.Services;

///// <summary>
///// Servicio para buscar la disponibilidad de un producto específico (por EAN)
///// en todas las tiendas cercanas a un punto geográfico.
///// </summary>
//public sealed class ProximityAvailabilityService
//{
//    private readonly IServiceProvider _serviceProvider;
//    private readonly ILogger<ProximityAvailabilityService> _log;
//    private readonly IVtexCookieManager _cookieManager;
//    private readonly IConfiguration _config;

//    public ProximityAvailabilityService(
//        IServiceProvider serviceProvider,
//        ILogger<ProximityAvailabilityService> log,
//        IConfiguration config,
//        IVtexCookieManager cookieManager)
//    {
//        _serviceProvider = serviceProvider;
//        _log = log;
//        _config = config;
//        _cookieManager = cookieManager;
//    }

//    public async Task<List<ProximitySearchResult>> CheckAvailabilityNearPointAsync(ProximitySearchRequest request, CancellationToken ct)
//    {
//        _log.LogInformation("Iniciando búsqueda por proximidad para EAN {EAN} cerca de ({Lat}, {Lon})", request.Ean, request.Latitude, request.Longitude);

//        var finalResults = new List<ProximitySearchResult>();

//        await using var scope = _serviceProvider.CreateAsyncScope();
//        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

//        // 1. Obtener todos los retailers activos
//        var retailers = await GetEnabledRetailersAsync(db, ct);

//        foreach (var retailer in retailers)
//        {
//            if (ct.IsCancellationRequested) break;

//            _log.LogInformation("Buscando en retailer: {Host}", retailer.VtexHost);

//            // 2. Para cada retailer, buscar el producto por EAN
//            var availableProduct = await GetAvailableProductForRetailerAsync(db, retailer.VtexHost, request.Ean, ct);
//            if (availableProduct is null)
//            {
//                _log.LogWarning("EAN {EAN} no encontrado en el catálogo de {Host}", request.Ean, retailer.VtexHost);
//                continue;
//            }

//            // 3. Encontrar tiendas de este retailer dentro del radio
//            var nearbyStores = await GetNearbyStoresAsync(db, retailer.RetailerId, request.Latitude, request.Longitude, request.RadiusMeters, ct);
//            if (!nearbyStores.Any())
//            {
//                _log.LogInformation("No se encontraron tiendas de {Host} en el radio de {Radius} metros.", retailer.VtexHost, request.RadiusMeters);
//                continue;
//            }

//            _log.LogInformation("Se encontraron {Count} tiendas de {Host} cerca.", nearbyStores.Count, retailer.VtexHost);

//            // 4. Testear disponibilidad en paralelo para las tiendas cercanas
//            var tasks = nearbyStores.Select(store => TestStoreAvailabilityAsync(retailer, availableProduct, store, ct));
//            var results = await Task.WhenAll(tasks);

//            finalResults.AddRange(results.Where(r => r is not null)!);
//        }

//        // 5. Ordenar los resultados finales por distancia
//        return finalResults.OrderBy(r => r.DistanceMeters).ToList();
//    }

//    private async Task<ProximitySearchResult?> TestStoreAvailabilityAsync(RetailerInfo retailer, AvailableProduct product, StoreInfo store, CancellationToken ct)
//    {
//        var salesChannel = retailer.SalesChannels.FirstOrDefault(1);
//        await SetupCookiesAsync(retailer.VtexHost, salesChannel);
//        using var httpClient = CreateHttpClientWithProxyAndCookies(retailer.VtexHost);

//        var improvedAvailabilityService = _serviceProvider.GetRequiredService<ImprovedAvailabilityService>() ?? throw new Exception("No se pudo crear el servicio");
//        // Reutilizamos la lógica de sondeo de ImprovedAvailabilityService
//        var availabilityResult = await improvedAvailabilityService.TestAvailabilityAsync(httpClient, retailer.VtexHost, salesChannel, product, store, ct);

//        if (availabilityResult.IsAvailable)
//        {
//            _log.LogInformation("¡Producto EAN {EAN} DISPONIBLE en {StoreName}!", product.EAN, store.StoreName);
//            return new ProximitySearchResult
//            {
//                RetailerHost = retailer.VtexHost,
//                StoreName = store.StoreName,
//                Address = $"{store.Street} {store.Number}, {store.City}",
//                DistanceMeters = CalculateDistance(store.Latitude, store.Longitude, -33.512661, -66.667128), // Usando las coordenadas de tu ejemplo
//                Price = availabilityResult.Price,
//                ListPrice = availabilityResult.ListPrice,
//                AvailableQuantity = availabilityResult.AvailableQuantity,
//                FoundPickupPointId = availabilityResult.FoundPickupPointId
//            };
//        }

//        return null;
//    }

//    // El resto de este archivo contiene métodos de ayuda que ya existen en ImprovedAvailabilityService
//    // Los he copiado aquí para que el servicio sea autocontenido.

//    private async Task<List<RetailerInfo>> GetEnabledRetailersAsync(AppDb db, CancellationToken ct)
//    {
//        var rawData = await (
//            from retailer in db.Retailers.AsNoTracking()
//            join config in db.VtexRetailersConfigs.AsNoTracking() on retailer.RetailerId equals config.RetailerId
//            where config.Enabled && retailer.IsActive
//            select new
//            {
//                retailer.RetailerId,
//                retailer.DisplayName,
//                VtexHost = retailer.VtexHost ?? retailer.PublicHost!,
//                SalesChannelsString = config.SalesChannels,
//            }
//        ).ToListAsync(ct);

//        return rawData.Select(r => new RetailerInfo
//        {
//            RetailerId = r.RetailerId,
//            DisplayName = r.DisplayName,
//            VtexHost = r.VtexHost,
//            SalesChannels = r.SalesChannelsString
//                .Split(',', StringSplitOptions.RemoveEmptyEntries)
//                .Select(s => int.TryParse(s.Trim(), out var result) ? result : 1)
//                .ToArray(),
//        }).ToList();
//    }

//    private async Task<AvailableProduct?> GetAvailableProductForRetailerAsync(AppDb db, string host, string ean, CancellationToken ct)
//    {
//        return await (
//            from seller in db.Sellers.AsNoTracking()
//            where seller.Sku.RetailerHost == host && seller.Sku.Ean == ean
//            select new AvailableProduct
//            {
//                EAN = seller.Sku.Ean!,
//                SkuId = seller.Sku.ItemId,
//                SellerId = seller.SellerId,
//                ProductName = seller.Sku.Product.ProductName ?? "N/A",
//                Owner = "N/A"
//            }
//        ).FirstOrDefaultAsync(ct);
//    }

//    private async Task<List<StoreInfo>> GetNearbyStoresAsync(AppDb db, string retailerId, double lat, double lon, int radiusMeters, CancellationToken ct)
//    {
//        var allStores = await db.Stores
//            .AsNoTracking()
//            .Where(s => s.RetailerId == retailerId && s.IsActive && s.PostalCode != null)
//            .ToListAsync(ct);

//        return allStores
//            .Select(s => new { Store = s, Distance = CalculateDistance((double)s.Latitude, (double)s.Longitude, lat, lon) })
//            .Where(s => s.Distance <= radiusMeters)
//            .OrderBy(s => s.Distance)
//            .Select(s => new StoreInfo
//            {
//                StoreId = s.Store.StoreId,
//                StoreName = s.Store.StoreName,
//                City = s.Store.City,
//                Province = s.Store.Province,
//                PostalCode = s.Store.PostalCode!,
//                VtexPickupPointId = s.Store.VtexPickupPointId,
//                Latitude = (double)s.Store.Latitude,
//                Longitude = (double)s.Store.Longitude,
//                Street = s.Store.Street,
//                Number = s.Store.StreetNumber
//            })
//            .ToList();
//    }

//    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
//    {
//        const double R = 6371e3; // Radio de la Tierra en metros
//        var phi1 = lat1 * Math.PI / 180;
//        var phi2 = lat2 * Math.PI / 180;
//        var deltaPhi = (lat2 - lat1) * Math.PI / 180;
//        var deltaLambda = (lon2 - lon1) * Math.PI / 180;

//        var a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2) +
//                Math.Cos(phi1) * Math.Cos(phi2) *
//                Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);
//        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

//        return R * c;
//    }

//    private HttpClient CreateHttpClientWithProxyAndCookies(string host)
//    {
//        var cookieContainer = _cookieManager.GetCookieContainer(host);
//        var proxyConfig = _config.GetSection("Proxy");

//        var handler = new HttpClientHandler()
//        {
//            CookieContainer = cookieContainer,
//            UseCookies = true,
//            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
//        };

//        var proxyUrl = proxyConfig["Url"];
//        if (!string.IsNullOrEmpty(proxyUrl))
//        {
//            var proxy = new WebProxy(new Uri(proxyUrl));
//            var username = proxyConfig["Username"];
//            if (!string.IsNullOrEmpty(username))
//            {
//                proxy.Credentials = new NetworkCredential(username, proxyConfig["Password"]);
//            }
//            handler.Proxy = proxy;
//            handler.UseProxy = true;
//        }

//        var client = new HttpClient(handler);
//        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
//        return client;
//    }

//    private async Task SetupCookiesAsync(string host, int salesChannel)
//    {
//        using var tempClient = CreateHttpClientWithProxyAndCookies(host);
//        await _cookieManager.WarmupCookiesAsync(tempClient, host);
//        _cookieManager.UpdateSegmentCookie(host, salesChannel);
//    }
//}
