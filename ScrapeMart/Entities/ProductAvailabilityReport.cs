namespace ScrapeMart.Entities;

public class ProductAvailabilityReport
{
    // Ubicación y Tienda
    public string? Provincia { get; set; }
    public string? Localidad { get; set; }
    public string? Canal { get; set; }
    public string? SubCanal { get; set; }
    public string? Empresa { get; set; }
    public string? TipoTienda { get; set; }
    public string? Tienda { get; set; }
    public double SucursalesLatitud { get; set; }
    public double SucursalesLongitud { get; set; }

    // Info de la Captura
    public DateTime Fecha { get; set; }

    // Info del Producto
    public string? Ean { get; set; }
    public string? Marca { get; set; }
    public string? Categoria { get; set; }
    public string? Subcategoria { get; set; }
    public string? Categoria3 { get; set; }
    public string? Producto { get; set; }
    //PRECIOS
    public decimal? PrecioVenta { get; set; }
    public decimal? PrecioLista { get; set; }
    public decimal? PrecioPromocion { get; set; }
    public decimal? PorcentajeDescuento { get; set; }
    public decimal? AhorroEnPesos { get; set; }
    public string? Moneda { get; set; }

    // Info de Disponibilidad
    public int? Stock { get; set; }
    public string? Cobertura { get; set; }

    // IDs
    public int ProductId { get; set; }
    public string SkuId { get; set; }
    public string PickupPointId { get; set; }
}