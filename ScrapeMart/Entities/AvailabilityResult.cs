using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ScrapeMart.Entities;

/// <summary>
/// Entidad para la tabla AvailabilityResults
/// Almacena los resultados de disponibilidad por StoreId (no por PickupPointId)
/// </summary>
[Table("AvailabilityResults")]
public sealed class AvailabilityResult
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string RetailerHost { get; set; } = default!;

    public long StoreId { get; set; }

    [Required]
    [MaxLength(20)]
    public string ProductEAN { get; set; } = default!;

    [Required]
    [MaxLength(50)]
    public string SkuId { get; set; } = default!;

    [Required]
    [MaxLength(50)]
    public string SellerId { get; set; } = default!;

    public int SalesChannel { get; set; }

    public bool IsAvailable { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Price { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ListPrice { get; set; }

    public int? AvailableQuantity { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "ARS";

    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    [Column(TypeName = "nvarchar(max)")]
    public string? RawResponse { get; set; }

    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    [ForeignKey(nameof(StoreId))]
    public Store? Store { get; set; }
}