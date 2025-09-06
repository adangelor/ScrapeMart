// File: Endpoints/RetailerAvailabilityEndpoints.cs
using Microsoft.AspNetCore.Mvc;
using ScrapeMart.Services;

namespace ScrapeMart.Endpoints;

public static class RetailerAvailabilityEndpoints
{
    public static IEndpointRouteBuilder MapRetailerAvailabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/retailers/{retailer}/skus/{skuId}/stores-availability",
            async ([FromServices] IHttpClientFactory httpFactory,
                   string retailer,
                   string skuId,
                   [FromBody] AvailabilityProbeRequest request,
                   CancellationToken ct) =>
            {
                var baseUri = RetailerDomainResolver.Resolve(retailer);
                if (baseUri is null) return Results.BadRequest(new { error = "retailer_not_supported", retailer });
                if (string.IsNullOrWhiteSpace(request.SellerId)) return Results.BadRequest(new { error = "sellerId_required" });

                var http = httpFactory.CreateClient(nameof(VtexPublicClient));
                http.BaseAddress = new Uri(baseUri);
                var client = new VtexPublicClient(http);

                var pickupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pointsGeo = new Dictionary<string, (double lon, double lat)>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (request.PickupPointIds is { Count: > 0 })
                    {
                        foreach (var id in request.PickupPointIds)
                            if (!string.IsNullOrWhiteSpace(id)) pickupIds.Add(id);
                    }

                    if (request.Lon.HasValue && request.Lat.HasValue)
                    {
                        var salesChannelInt = int.TryParse(request.SalesChannel, out var sc) ? sc : (int?)null;
                        var ptsGeo = await client.GetPickupPointsByGeoAsync(request.Lon.Value, request.Lat.Value, salesChannelInt, ct);
                        foreach (var p in ptsGeo)
                        {
                            if (string.IsNullOrWhiteSpace(p.Id)) continue;
                            pickupIds.Add(p.Id!);
                            if (p.GeoCoordinates is { Length: 2 })
                                pointsGeo[p.Id!] = (p.GeoCoordinates[0], p.GeoCoordinates[1]);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(request.PostalCode))
                    {
                        var salesChannelInt = int.TryParse(request.SalesChannel, out var sc) ? sc : (int?)null;
                        var ptsPostal = await client.GetPickupPointsByPostalAsync(request.PostalCode!, request.CountryCode ?? "AR", salesChannelInt, ct);
                        foreach (var p in ptsPostal)
                        {
                            if (string.IsNullOrWhiteSpace(p.Id)) continue;
                            pickupIds.Add(p.Id!);
                            if (p.GeoCoordinates is { Length: 2 })
                                pointsGeo[p.Id!] = (p.GeoCoordinates[0], p.GeoCoordinates[1]);
                        }
                    }
                }
                catch (VtexHttpException vex)
                {
                    return Results.Problem(
                        title: "VTEX pickup discovery failed",
                        detail: vex.RawBody,
                        statusCode: (int)vex.StatusCode,
                        extensions: new Dictionary<string, object?> { ["context"] = vex.Context });
                }

                // 1) Si hay pickups, simulamos por pickup.
                if (pickupIds.Count > 0)
                {
                    var tasks = pickupIds.Select(async id =>
                    {
                        (double? lon, double? lat) geoForThis =
                            pointsGeo.TryGetValue(id, out var g) ? (g.lon, g.lat)
                            : (request.Lon, request.Lat);

                        try
                        {
                            var (available, price, listPrice) = await VtexAvailabilityService.ProbePickupAsync(
                                client,
                                skuId: skuId,
                                sellerId: request.SellerId!,
                                postal: request.PostalCode,
                                geo: geoForThis,
                                salesChannel: request.SalesChannel,
                                pickupPointId: id,
                                ct: ct);

                            return new AvailabilityProbeItem
                            {
                                PickupPointId = id,
                                IsAvailable = available,
                                Price = price,
                                ListPrice = listPrice
                            };
                        }
                        catch
                        {
                            return new AvailabilityProbeItem
                            {
                                PickupPointId = id,
                                IsAvailable = false,
                                Price = null,
                                ListPrice = null
                            };
                        }
                    });

                    var results = await Task.WhenAll(tasks);

                    return Results.Ok(new AvailabilityProbeResponse
                    {
                        Items = results
                            .OrderByDescending(x => x.IsAvailable)
                            .ThenBy(x => x.PickupPointId, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        Delivery = new()
                    });
                }

                // 2) Fallback: sin pickups en la zona → probamos DELIVERY por seller de la región.
                if (string.IsNullOrWhiteSpace(request.PostalCode))
                    return Results.Ok(new AvailabilityProbeResponse { Items = [], Delivery = [] });

                List<VtexPublicClient.SellerDto> sellers;
                try
                {
                    var salesChannelInt = int.TryParse(request.SalesChannel, out var sc) ? sc : (int?)null;
                    sellers = await client.GetRegionSellersByPostalAsync(request.PostalCode!, request.CountryCode ?? "AR", salesChannelInt, ct);
                }
                catch (VtexHttpException vex)
                {
                    return Results.Problem(
                        title: "VTEX regions failed",
                        detail: vex.RawBody,
                        statusCode: (int)vex.StatusCode,
                        extensions: new Dictionary<string, object?> { ["context"] = vex.Context });
                }

                if (sellers.Count == 0)
                    return Results.Ok(new AvailabilityProbeResponse { Items = [], Delivery = [] });

                var deliveryTasks = sellers.Select(async s =>
                {
                    try
                    {
                        // Usar el nuevo método de simulación de delivery
                        var salesChannelInt = int.TryParse(request.SalesChannel, out var sc) ? sc : 1;
                        var deliveryResult = await client.SimulateDeliveryAsync(
                            salesChannelInt,
                            skuId,
                            1,
                            s.Id,
                            request.CountryCode ?? "AR",
                            request.PostalCode!,
                            ct);

                        return new DeliveryProbeItem
                        {
                            SellerId = s.Id,
                            SellerName = s.Name,
                            IsDeliverable = deliveryResult.Available,
                            Price = deliveryResult.Price,
                            ListPrice = deliveryResult.ListPrice
                        };
                    }
                    catch
                    {
                        return new DeliveryProbeItem
                        {
                            SellerId = s.Id,
                            SellerName = s.Name,
                            IsDeliverable = false,
                            Price = null,
                            ListPrice = null
                        };
                    }
                });

                var delivery = await Task.WhenAll(deliveryTasks);

                return Results.Ok(new AvailabilityProbeResponse
                {
                    Items = [],
                    Delivery = delivery
                        .OrderByDescending(d => d.IsDeliverable)
                        .ThenBy(d => d.SellerId, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                });
            })
            .WithName("ProbeSkuAvailabilityByStores")
            .WithSummary("Disponibilidad por sucursal (pickup) y fallback por delivery si no hay pickups")
            .WithDescription("Detecta pickup points; si no hay, consulta sellers por región y simula entrega a domicilio.");

        return app;
    }
}
public static class RetailerDomainResolver
{
    public static string? Resolve(string retailer) => retailer.ToLowerInvariant() switch
    {
        "vea" => "https://www.vea.com.ar/",
        "jumbo" => "https://www.jumbo.com.ar/",
        "disco" => "https://www.disco.com.ar/",
        "carrefour" => "https://www.carrefour.com.ar/",
        "libertad" => "https://www.hiperlibertad.com.ar/",
        _ => null
    };
}

public sealed class AvailabilityProbeRequest
{
    public string? SellerId { get; set; }
    public string? SalesChannel { get; set; } = "1";
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; } = "AR";
    public double? Lon { get; set; }
    public double? Lat { get; set; }
    public List<string>? PickupPointIds { get; set; }
}

public sealed class AvailabilityProbeResponse
{
    public List<AvailabilityProbeItem> Items { get; set; } = new();
    public List<DeliveryProbeItem> Delivery { get; set; } = new();
}

public sealed class AvailabilityProbeItem
{
    public string PickupPointId { get; set; } = default!;
    public bool IsAvailable { get; set; }
    public decimal? Price { get; set; }
    public decimal? ListPrice { get; set; }
}

public sealed class DeliveryProbeItem
{
    public string SellerId { get; set; } = default!;
    public string? SellerName { get; set; }
    public bool IsDeliverable { get; set; }
    public decimal? Price { get; set; }
    public decimal? ListPrice { get; set; }
}