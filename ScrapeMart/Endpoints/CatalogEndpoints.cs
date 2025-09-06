// File: Endpoints/CatalogEndpoints.cs
using Microsoft.AspNetCore.Mvc; // <-- PASO 1: AÑADIR ESTE USING
using ScrapeMart.Services;

namespace ScrapeMart.Endpoints
{
    public static class CatalogEndpoints
    {
        public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
        {
            group.MapGet("/products/{productId}",
                // PASO 2: AÑADIR [FromServices]
                async ([FromServices] ICatalogQueryService svc, string productId, CancellationToken ct) =>
                {
                    var res = await svc.GetProductByIdAsync(productId, ct);
                    return res is null ? Results.NotFound() : Results.Ok(res);
                })
                .WithName("GetProductById")
                .WithSummary("Obtiene un producto por productId");

            group.MapGet("/skus/by-ean/{ean}",
                async ([FromServices] ICatalogQueryService svc, string ean, CancellationToken ct) =>
                {
                    var res = await svc.GetSkuByEanAsync(ean, ct);
                    return res is null ? Results.NotFound() : Results.Ok(res);
                })
                .WithName("GetSkuByEan")
                .WithSummary("Obtiene un SKU por EAN");

            group.MapGet("/categories/{categoryId:int}/products",
                async ([FromServices] ICatalogQueryService svc, int categoryId, int page, int pageSize, CancellationToken ct) =>
                {
                    var res = await svc.GetProductsByCategoryAsync(categoryId, page, pageSize, ct);
                    return Results.Ok(res);
                })
                .WithName("GetProductsByCategory")
                .WithSummary("Lista productos de una categoría");

            group.MapGet("/categories/{categoryId:int}/breadcrumb",
                async ([FromServices] ICatalogQueryService svc, int categoryId, CancellationToken ct) =>
                {
                    var res = await svc.GetCategoryBreadcrumbAsync(categoryId, ct);
                    return Results.Ok(res);
                })
                .WithName("GetCategoryBreadcrumb")
                .WithSummary("Breadcrumb de categoría");

            group.MapGet("/offers/history/{skuDbId:int}",
                async ([FromServices] ICatalogQueryService svc, int skuDbId, int take, CancellationToken ct) =>
                {
                    var res = await svc.GetOfferHistoryBySkuAsync(skuDbId, take, ct);
                    return Results.Ok(res);
                })
                .WithName("GetOfferHistoryBySku")
                .WithSummary("Histórico de ofertas por SKU");

            group.MapGet("/search",
                async ([FromServices] ICatalogQueryService svc, string q, int page, int pageSize, CancellationToken ct) =>
                {
                    var res = await svc.SearchProductsAsync(q, page, pageSize, ct);
                    return Results.Ok(res);
                })
                .WithName("SearchProducts")
                .WithSummary("Búsqueda en el catálogo persistido");

            return group;
        }
    }
}