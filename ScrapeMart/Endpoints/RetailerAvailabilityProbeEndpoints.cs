using ScrapeMart.Services;

namespace ScrapeMart.Endpoints;

public static class RetailerAvailabilityProbeEndpoints
{
    public sealed record ProbeArgs(
        string SellerId,
        int SalesChannel,
        string CountryCode,
        string PostalCode,
        string PickupPointId);

    public sealed record ProbeOut(
        bool IsAvailable,
        int MaxFeasibleQty,
        decimal? Price,
        string Currency);

    public static IEndpointRouteBuilder MapRetailerAvailabilityProbe(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/retailers/{hostEncoded}/skus/{skuId}/probe")
                   .WithTags("Retailers: VTEX")
                   .WithOpenApi();

        g.MapPost("", async (
                string hostEncoded,
                string skuId,
                ProbeArgs args,
                VtexAvailabilityProbeService svc,
                CancellationToken ct) =>
        {
            // hostEncoded vendría como base64url o Uri.EscapeDataString si preferís; si ya pasás el host literal, quitá esto
            var host = Uri.UnescapeDataString(hostEncoded);

            var result = await svc.ProbePickupAsync(
                host: host,
                salesChannel: args.SalesChannel,
                skuId: skuId,
                sellerId: args.SellerId,
                pickupPointId: args.PickupPointId,
                countryCode: args.CountryCode,
                postalCode: args.PostalCode,
                ct: ct);

            return Results.Ok(new ProbeOut(result.IsAvailable, result.MaxFeasibleQty, result.Price, result.Currency));
        });

        return app;
    }
}
