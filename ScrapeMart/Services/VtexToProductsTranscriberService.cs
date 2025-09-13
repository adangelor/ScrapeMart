using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities;
using ScrapeMart.Storage;
using System.Data;
using System.Linq;
using System.Text.Json;

namespace ScrapeMart.Services;

public sealed class VtexToProductsTranscriberService
{
    private readonly AppDb _db;
    private readonly string _sqlConn;
    private readonly ILogger<VtexToProductsTranscriberService> _log;

    public VtexToProductsTranscriberService(
        AppDb db,
        IConfiguration cfg,
        ILogger<VtexToProductsTranscriberService> log)
    {
        _db = db;
        _sqlConn = cfg.GetConnectionString("Default")!;
        _log = log;
    }

    /// <summary>
    /// Transcribe productos desde las tablas VTEX raw hacia la tabla Products definitiva
    /// </summary>
    public async Task<TranscriptionResult> TranscribeProductsAsync(
        string host,
        int batchSize = 100,
        CancellationToken ct = default)
    {
        var result = new TranscriptionResult { Host = host };

        _log.LogInformation("Iniciando transcripción de productos VTEX para host: {Host}", host);

        try
        {
            // Obtener productos de las tablas raw VTEX que no han sido procesados recientemente
            var vtexProducts = await GetVtexProductsToTranscribeAsync(host, ct);
            result.TotalVtexProducts = vtexProducts.Count;

            _log.LogInformation("Encontrados {Count} productos VTEX para transcribir", vtexProducts.Count);

            if (vtexProducts.Count == 0)
            {
                _log.LogInformation("No hay productos VTEX para transcribir en host: {Host}", host);
                return result;
            }

            // Procesar en lotes
            for (int i = 0; i < vtexProducts.Count; i += batchSize)
            {
                var batch = vtexProducts.Skip(i).Take(batchSize).ToList();
                await ProcessBatchAsync(host, batch, result, ct);

                _log.LogInformation("Procesado lote {Current}/{Total}",
                    Math.Min(i + batchSize, vtexProducts.Count), vtexProducts.Count);

                if (ct.IsCancellationRequested)
                {
                    _log.LogWarning("Transcripción cancelada por el usuario");
                    break;
                }
            }

            _log.LogInformation("Transcripción completada para {Host}. Procesados: {Processed}, Nuevos: {New}, Actualizados: {Updated}, Errores: {Errors}",
                host, result.ProductsProcessed, result.ProductsInserted, result.ProductsUpdated, result.ProductsWithErrors);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error durante la transcripción para host: {Host}", host);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<List<VtexProductData>> GetVtexProductsToTranscribeAsync(string host, CancellationToken ct)
    {
        const string sql = @"
            SELECT 
                vp.ProductId,
                vp.ProductName,
                vp.Brand,
                vp.LinkText,
                vp.Categories,
                vp.FirstSeenUtc,
                vp.LastSeenUtc
            FROM dbo.VtexProducts vp
            WHERE vp.RetailerHost = @host
            AND vp.LastSeenUtc >= DATEADD(hour, -36, SYSUTCDATETIME()) -- Solo productos vistos en las últimas 24h
            ORDER BY vp.LastSeenUtc DESC";

        var products = new List<VtexProductData>();

        await using var cn = new SqlConnection(_sqlConn);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@host", host);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            products.Add(new VtexProductData
            {
                ProductId = reader.GetInt32("ProductId"),
                ProductName = reader.IsDBNull("ProductName") ? null : reader.GetString("ProductName"),
                Brand = reader.IsDBNull("Brand") ? null : reader.GetString("Brand"),
                LinkText = reader.IsDBNull("LinkText") ? null : reader.GetString("LinkText"),
                Categories = reader.IsDBNull("Categories") ? null : reader.GetString("Categories"),
                FirstSeenUtc = reader.IsDBNull("FirstSeenUtc") ? null : reader.GetDateTime("FirstSeenUtc"),
                LastSeenUtc = reader.GetDateTime("LastSeenUtc")
            });
        }

        return products;
    }

    private async Task ProcessBatchAsync(
        string host,
        List<VtexProductData> batch,
        TranscriptionResult result,
        CancellationToken ct)
    {
        var productIds = batch.Select(p => p.ProductId).ToList();

        // Obtener productos existentes en la tabla definitiva
        var existingProducts = await _db.Products
            .Where(p => p.RetailerHost == host && productIds.Contains(p.ProductId))
            .ToDictionaryAsync(p => p.ProductId, p => p, ct);

        foreach (var vtexProduct in batch)
        {
            try
            {
                var isNew = !existingProducts.TryGetValue(vtexProduct.ProductId, out var product);

                if (isNew)
                {
                    product = new Product
                    {
                        ProductId = vtexProduct.ProductId,
                        RetailerHost = host
                    };
                    _db.Products.Add(product);
                    result.ProductsInserted++;
                }
                else
                {
                    result.ProductsUpdated++;
                }

                // Mapear campos desde VTEX a Product
                product.ProductName = vtexProduct.ProductName;
                product.Brand = vtexProduct.Brand;
                product.LinkText = vtexProduct.LinkText;

                // Si hay categorías en formato "cat1 | cat2 | cat3", tomar la primera
                if (!string.IsNullOrEmpty(vtexProduct.Categories))
                {
                    var categories = vtexProduct.Categories.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
                    if (categories.Length > 0)
                    {
                        // Limpiar cualquier barra inicial/final de la categoría
                        product.CategoryId = categories[0].Trim('/');
                    }
                }

                // Establecer fecha de release si es la primera vez que vemos el producto
                if (isNew && vtexProduct.FirstSeenUtc.HasValue)
                {
                    product.ReleaseDateUtc = vtexProduct.FirstSeenUtc.Value;
                }

                // Crear JSON con datos de origen para auditoría
                var originData = new
                {
                    source = "vtex_scraper",
                    originalCategories = vtexProduct.Categories,
                    firstSeenInVtex = vtexProduct.FirstSeenUtc,
                    lastSeenInVtex = vtexProduct.LastSeenUtc,
                    transcribedAt = DateTime.UtcNow,
                    transcriptionVersion = "1.0"
                };
                product.RawJson = JsonSerializer.Serialize(originData, new JsonSerializerOptions { WriteIndented = false });

                result.ProductsProcessed++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error procesando producto {ProductId} de host {Host}",
                    vtexProduct.ProductId, host);
                result.ProductsWithErrors++;
            }
        }

        try
        {
            // Guardar el lote completo
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error guardando lote para host {Host}", host);
            // Revertir contadores si falló el guardado
            var batchSize = batch.Count;
            result.ProductsProcessed = Math.Max(0, result.ProductsProcessed - batchSize);
            result.ProductsInserted = Math.Max(0, result.ProductsInserted - batchSize);
            result.ProductsUpdated = Math.Max(0, result.ProductsUpdated - batchSize);
            result.ProductsWithErrors += batchSize;
            throw;
        }
    }

    /// <summary>
    /// Datos de producto extraídos de las tablas VTEX raw
    /// </summary>
    private sealed class VtexProductData
    {
        public int ProductId { get; set; } = default!;
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public string? LinkText { get; set; }
        public string? Categories { get; set; }
        public DateTime? FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    /// <summary>
    /// Resultado de la operación de transcripción
    /// </summary>
    public sealed class TranscriptionResult
    {
        public string Host { get; set; } = default!;
        public int TotalVtexProducts { get; set; }
        public int ProductsProcessed { get; set; }
        public int ProductsInserted { get; set; }
        public int ProductsUpdated { get; set; }
        public int ProductsWithErrors { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt - StartedAt : null;

        public void MarkCompleted()
        {
            CompletedAt = DateTime.UtcNow;
        }
    }
}

// Método de extensión para integrar con tu VtexProductSweepService
public static class VtexProductSweepServiceExtensions
{
    /// <summary>
    /// Extensión para transcribir productos después del sweep
    /// </summary>
    public static async Task<VtexToProductsTranscriberService.TranscriptionResult> TranscribeToProductsAsync(
        this VtexProductSweepService.SweepResult sweepResult,
        IServiceProvider serviceProvider,
        int batchSize = 100,
        CancellationToken ct = default)
    {
        using var scope = serviceProvider.CreateScope();
        var transcriber = scope.ServiceProvider.GetRequiredService<VtexToProductsTranscriberService>();
        var result = await transcriber.TranscribeProductsAsync(sweepResult.Host, batchSize, ct);
        result.MarkCompleted();
        return result;
    }
}

// Para registrar en Program.cs:
// builder.Services.AddScoped<VtexToProductsTranscriberService>();