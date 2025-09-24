using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ScrapeMart.Entities
{
    [Table("Stores")]
    public sealed class Store
    {
        [Key]
        public long StoreId { get; set; }

        [Required]
        [MaxLength(50)]
        public string RetailerId { get; set; } = default!;

        [Required]
        [MaxLength(300)]
        public string StoreName { get; set; } = default!;

        [MaxLength(100)]
        public string? StoreType { get; set; }

        [Required]
        [MaxLength(300)]
        public string Street { get; set; } = default!;

        [Required]
        [MaxLength(50)]
        public string StreetNumber { get; set; } = default!;

        [MaxLength(200)]
        public string? Neighborhood { get; set; }

        [Required]
        [MaxLength(200)]
        public string City { get; set; } = default!;

        [Required]
        [MaxLength(100)]
        public string Province { get; set; } = default!;

        [MaxLength(20)]
        public string? PostalCode { get; set; }

        [Column(TypeName = "decimal(10, 8)")]
        public decimal Latitude { get; set; }

        [Column(TypeName = "decimal(11, 8)")]
        public decimal Longitude { get; set; }

        public string? BusinessHours { get; set; }
        public string? Notes { get; set; }

        public int SourceIdBandera { get; set; }
        public int SourceIdComercio { get; set; }
        public int SourceIdSucursal { get; set; }

        [MaxLength(200)]
        public string? VtexPickupPointId { get; set; }

        [MaxLength(200)]
        public string? VtexStoreId { get; set; }

        public DateTime? LastVtexSync { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public Retailer? Retailer { get; set; }
    }
}