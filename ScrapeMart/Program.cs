using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;
using ScrapeMart.Services;
using ScrapeMart.Endpoints;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// ===== CONFIGURACIÓN DE SERVICIOS =====

// Base services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<AppDb>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// HTTP Clients
builder.Services.AddHttpClient("vtexSession", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
});

builder.Services.AddHttpClient(nameof(VtexPublicClient), client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
});

// Business Services
builder.Services.AddScoped<VtexCatalogClient>(provider =>
{
    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("vtexSession");
    return new VtexCatalogClient(httpClient);
});

builder.Services.AddScoped<VtexPublicClient>(provider =>
{
    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    return new VtexPublicClient(httpClientFactory);
});

builder.Services.AddScoped<CatalogSyncService>();
builder.Services.AddScoped<VtexProductSweepService>();
builder.Services.AddScoped<VtexSweepService>();
builder.Services.AddScoped<VtexAvailabilityProbeService>();
builder.Services.AddScoped<AvailabilityOrchestratorService>();
builder.Services.AddScoped<BrandScrapingService>();
builder.Services.AddScoped<TargetedScrapingService>();
builder.Services.AddScoped<CatalogOrchestratorService>();
builder.Services.AddScoped<MasterOrchestratorService>();
builder.Services.AddScoped<VtexToProductsTranscriberService>();
builder.Services.AddScoped<IRetailerConfigService, RetailerConfigService>();
builder.Services.AddScoped<ComprehensiveAvailabilityService>();
builder.Services.AddScoped<OptimizedAvailabilityService>();
builder.Services.AddScoped<BasicDebuggingService>();
builder.Services.AddCatalogQueryService();
builder.Services.AddScoped<VtexEndpointTesterService>();
builder.Services.AddScoped<WorkingVtexFlowService>();
builder.Services.AddScoped<VtexOrderFormService>();
builder.Services.AddScoped<VtexSessionService>();
builder.Services.AddScoped<VtexApiTester>();
builder.Services.AddScoped<VtexProxyService>();
builder.Services.AddScoped<VtexSimpleSessionService>();
builder.Services.AddScoped<VtexSimulationService>();
builder.Services.AddSingleton<IVtexCookieManager, VtexCookieManager>();
var app = builder.Build();

// ===== CONFIGURACIÓN DEL PIPELINE =====

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// ===== REGISTRO DE ENDPOINTS =====

// Catalog endpoints
var catalogGroup = app.MapGroup("/api/catalog")
    .WithTags("Catalog")
    .WithOpenApi();
catalogGroup.MapCatalogEndpoints();

// Retailer availability endpoints
app.MapRetailerAvailabilityEndpoints();
app.MapRetailerAvailabilityProbe();
// VTEX operations endpoints
app.MapVtexCatalogSweepEndpoints();

// Operations endpoints
var opsGroup = app.MapGroup("/api/ops")
    .WithTags("Operations")
    .WithOpenApi();
opsGroup.MapOperationsEndpoints();
opsGroup.MapDashboardEndpoints();
opsGroup.MapOrderFormEndpoints();


app.Run();