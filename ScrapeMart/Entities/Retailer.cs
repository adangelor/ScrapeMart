using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ScrapeMart.Entities
{
    [Table("Retailers")]
    public sealed class Retailer
    {
        [Key]
        [MaxLength(50)]
        public string RetailerId { get; set; } = default!;

        [Required]
        [MaxLength(200)]
        public string DisplayName { get; set; } = default!;

        [Required]
        [MaxLength(300)]
        public string CompanyName { get; set; } = default!;

        [Required]
        [MaxLength(50)]
        public string TaxId { get; set; } = default!;

        [MaxLength(500)]
        public string? WebsiteUrl { get; set; }

        public int SourceIdBandera { get; set; }
        public int SourceIdComercio { get; set; }

        [MaxLength(500)]
        public string? VtexHost { get; set; }

        [MaxLength(500)]
        public string? PublicHost { get; set; }

        [MaxLength(100)]
        public string SalesChannels { get; set; } = "1";

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public ICollection<Store> Stores { get; set; } = new List<Store>();
        public VtexRetailersConfig? VtexConfig { get; set; }    
    }
}