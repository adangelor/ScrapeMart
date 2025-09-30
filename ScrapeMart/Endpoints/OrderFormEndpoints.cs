// File: Endpoints/OrderFormEndpoints.cs
using Microsoft.AspNetCore.Mvc;
using ScrapeMart.Services;

namespace ScrapeMart.Endpoints;

public static class OrderFormEndpoints
{
    public static RouteGroupBuilder MapOrderFormEndpoints(this RouteGroupBuilder group)
    {
        // 🆕 Test usando simulación directa (evita el CHK003)
        group.MapPost("/test-simulation-availability",
            async ([FromServices] VtexSimulationService service,
                   [FromQuery] string host,
                   CancellationToken ct) =>
            {
                await service.ProbeAvailabilityWithSimulationAsync(host, ct);
                return Results.Ok(new { message = "Simulation availability test completed", host });
            })
            .WithName("TestSimulationAvailability")
            .WithSummary("Usa simulación directa en lugar del flujo completo de OrderForm")
            .WithDescription("Evita el CHK003 usando solo el endpoint de simulación");

        // 🆕 Test comparando PROXY vs DIRECTO
        group.MapPost("/test-proxy-vs-direct",
            async ([FromServices] VtexProxyService service,
                   [FromQuery] string host,
                   [FromQuery] int salesChannel = 1,
                   CancellationToken ct = default) =>
            {
                var result = await service.CompareProxyVsDirectAsync(host, salesChannel, ct);
                return Results.Ok(result);
            })
            .WithName("TestProxyVsDirect")
            .WithSummary("Compara conexión directa vs proxy de Bright Data")
            .WithDescription("Testea si el proxy de Bright Data ayuda a bypasear el bloqueo de VTEX");

        // 🆕 Test SIMPLE con GET y URLs correctas
        group.MapPost("/test-simple-orderform",
            async ([FromServices] VtexSimpleSessionService service,
                   [FromQuery] string host,
                   [FromQuery] string skuId,
                   [FromQuery] string sellerId,
                   [FromQuery] int salesChannel = 1,
                   CancellationToken ct = default) =>
            {
                var result = await service.TestSimpleAvailabilityAsync(host, skuId, sellerId, salesChannel, ct);
                return Results.Ok(result);
            })
            .WithName("TestSimpleOrderForm")
            .WithSummary("Test simple usando GET para orderForm")
            .WithDescription("Prueba el flujo más básico: GET orderForm → POST add item");

        // 🆕 Endpoint para diagnosticar APIs de VTEX
        group.MapPost("/test-vtex-apis",
            async ([FromServices] VtexApiTester tester,
                   [FromQuery] string host,
                   [FromQuery] int salesChannel = 1,
                   CancellationToken ct = default) =>
            {
                var results = await tester.TestOrderFormEndpointsAsync(host, salesChannel, ct);
                return Results.Ok(results);
            })
            .WithName("TestVtexApis")
            .WithSummary("Diagnostica las APIs de VTEX para ver cuáles funcionan")
            .WithDescription("Prueba diferentes endpoints y métodos HTTP para descubrir cómo funciona realmente VTEX");

        // Endpoint para testear disponibilidad usando el flujo real de OrderForm
        group.MapPost("/test-orderform-availability",
            async ([FromServices] VtexOrderFormService service,
                   [FromQuery] string host,
                   CancellationToken ct) =>
            {
                await service.ProbeAvailabilityWithOrderFormAsync(host, ct);
                return Results.Ok(new
                {
                    message = "OrderForm availability test completed",
                    host,
                    timestamp = DateTime.UtcNow
                });
            })
            .WithName("TestOrderFormAvailability")
            .WithSummary("Testea disponibilidad usando el flujo real de OrderForm de VTEX")
            .WithDescription("Simula el proceso completo: crear orderForm → agregar items → simular shipping → verificar disponibilidad");
        group.MapPost("/operations/availability/improved-check",
            async ([FromServices] ImprovedAvailabilityService service,
                   string? specificHost,
                   CancellationToken ct) =>
            {
                var result = await service.RunComprehensiveCheckAsync(specificHost, ct);
                return Results.Ok(result);
            })
            .WithName("RunImprovedAvailabilityCheck")
            .WithSummary("🚀 Verificación mejorada de disponibilidad")
            .WithDescription(@"
        ✨ MEJORAS PRINCIPALES:
        
        ✅ Sin cookies hardcodeadas (usa VtexCookieManager)
        ✅ Filtra productos con Track = true
        ✅ Control inteligente de velocidad (throttling)
        ✅ Usa proxy configurado en appsettings.json
        ✅ Reintentos automáticos con exponential backoff
        ✅ Mejor manejo de errores
        ✅ Logs detallados del progreso
        
        📊 PARÁMETROS:
        - specificHost: (opcional) Procesar solo una cadena específica
        
        Ejemplo: POST /operations/availability/improved-check?specificHost=https://www.vea.com.ar
    ")
            .WithTags("Availability - Improved");
        return group;
    }
}