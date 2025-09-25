using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ScrapeMart.Entities;

[Table("ProductsToTrack")]
public sealed class ProductToTrack
{
    [Key] // Esto le dice a Entity Framework que 'EAN' es la clave primaria. ¡La causa del error!
    [Column(TypeName = "varchar(20)")]
    public string EAN { get; set; } = default!;

    [Required]
    [Column(TypeName = "nvarchar(50)")]
    public string Owner { get; set; } = default!;

    [Column(TypeName = "nvarchar(255)")]
    public string? ProductName { get; set; }

    public bool? Track { get; set; }
}