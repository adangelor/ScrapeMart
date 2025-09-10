using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ScrapeMart.Entities;

[Table("Sucursales")]
[PrimaryKey(nameof(IdBandera), nameof(IdComercio), nameof(IdSucursal))] // <-- CLAVE COMPUESTA DEFINIDA
public sealed class Sucursal
{
    public int IdComercio { get; set; }
    public int IdBandera { get; set; }
    public int IdSucursal { get; set; }

    public string SucursalesNombre { get; set; } = default!;
    public string SucursalesCodigoPostal { get; set; } = default!;
    public double SucursalesLatitud { get; set; }
    public double SucursalesLongitud { get; set; }
}