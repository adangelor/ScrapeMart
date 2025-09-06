using ScrapeMart.Services;

namespace ScrapeMart.Endpoints;

public static class VtexCatalogSweepEndpoints
{

    public static IEndpointRouteBuilder MapVtexCatalogSweepEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/ops/vtex");
        grp.MapGet("/sweep-products", async (
            string host,
            string? ft,
            int? categoryId,
            int from,
            int to,
            int step,
            int? sc,
            VtexProductSweepService svc,
            CancellationToken ct) =>
        {
            var res = await svc.SweepAsync(
                host: host,
                ft: ft,
                categoryId: categoryId,
                from: from <= 0 ? 0 : from,
                to: to <= 0 ? 49 : to,
                step: step <= 0 ? 50 : step,
                sc: sc,
                ct: ct);

            return Results.Ok(res);
        })
        .WithName("VtexSweepProducts")
        .WithOpenApi();

        return app;
    }
}



