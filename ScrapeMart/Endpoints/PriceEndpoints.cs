using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Services;

namespace ScrapeMart.Endpoints;

public static class PriceEndpoints
{
    public static RouteGroupBuilder MapPriceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/by-ean/{ean}/retailer/{retailerHost}/store/{storeId}", GetPricesByEan)
            .WithName("GetPricesByEan")
            .WithSummary("Obtiene precios de un producto por EAN en una sucursal específica")
            .WithDescription("Consulta los precios más recientes de un producto identificado por su código EAN en una sucursal específica de una cadena comercial")
            .Produces<Entities.dtos.PriceQueryResponseDto>(200)
            .Produces(404);

        group.MapGet("/by-ean/{ean}/retailer/{retailerHost}", GetPricesByEanAllStores)
            .WithName("GetPricesByEanAllStores")
            .WithSummary("Obtiene precios de un producto por EAN en todas las sucursales de una cadena")
            .WithDescription("Consulta los precios más recientes de un producto identificado por su código EAN en todas las sucursales de una cadena comercial")
            .Produces<List<Entities.dtos.PriceQueryResponseDto>>(200)
            .Produces(404);

        return group;
    }

    private static async Task<IResult> GetPricesByEan(
        string ean,
        string retailerHost,
        string storeId,
        [FromServices] IPriceQueryService priceService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ean))
            return Results.BadRequest("EAN es requerido");

        if (string.IsNullOrWhiteSpace(retailerHost))
            return Results.BadRequest("RetailerHost es requerido");

        if (string.IsNullOrWhiteSpace(storeId))
            return Results.BadRequest("StoreId es requerido");

        var result = await priceService.GetPricesByEanAsync(ean, retailerHost, storeId, ct);
        
        return result is not null 
            ? Results.Ok(result) 
            : Results.NotFound($"No se encontraron precios para EAN {ean} en la sucursal {storeId} de {retailerHost}");
    }

    private static async Task<IResult> GetPricesByEanAllStores(
        string ean,
        string retailerHost,
        [FromServices] IPriceQueryService priceService,
        [FromServices] Storage.AppDb db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ean))
            return Results.BadRequest("EAN es requerido");

        if (string.IsNullOrWhiteSpace(retailerHost))
            return Results.BadRequest("RetailerHost es requerido");

        // Obtenemos todas las sucursales del retailer
        var storeIds = await db.Stores.AsNoTracking()
            .Where(s => s.Retailer.VtexHost == retailerHost)
            .Select(s => s.StoreId)
            .ToListAsync(ct);

        if (!storeIds.Any())
            return Results.NotFound($"No se encontraron sucursales para el retailer {retailerHost}");

        var results = new List<Entities.dtos.PriceQueryResponseDto>();

        // Consultamos precios para cada sucursal
        foreach (var storeId in storeIds)
        {
            var priceResult = await priceService.GetPricesByEanAsync(ean, retailerHost, storeId, ct);
            if (priceResult is not null && priceResult.Prices.Any())
            {
                results.Add(priceResult);
            }
        }

        return results.Any() 
            ? Results.Ok(results) 
            : Results.NotFound($"No se encontraron precios para EAN {ean} en ninguna sucursal de {retailerHost}");
    }
}