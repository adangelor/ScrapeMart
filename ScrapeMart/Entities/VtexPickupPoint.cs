using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace ScrapeMart.Entities;

[Table("VtexPickupPoints")]
[PrimaryKey(nameof(RetailerHost), nameof(PickupPointId))]
public sealed class VtexPickupPoint
{
    public string RetailerHost { get; set; } = default!;
    public string PickupPointId { get; set; } = default!;

    // --- ¡CAMPOS QUE FALTABAN, AÑADIDOS AQUÍ! ---
    public int? SourceIdBandera { get; set; }
    public int? SourceIdComercio { get; set; }
    // --- FIN DE LA CORRECCIÓN ---

    public int? SourceIdSucursal { get; set; }
}
