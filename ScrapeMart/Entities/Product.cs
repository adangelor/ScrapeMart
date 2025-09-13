using System.Text.Json.Serialization;

namespace ScrapeMart.Entities
{
    public sealed class Product
    {
        public int Id { get; set; }
        public string RetailerHost { get; set; } = default!; // <-- ¡CAMPO AÑADIDO!
        public int ProductId { get; set; } = default!;
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public int? BrandId { get; set; }
        public string? LinkText { get; set; }
        public string? Link { get; set; }
        public DateTime? ReleaseDateUtc { get; set; }
        public string? CacheId { get; set; }
        public string? CategoryId { get; set; }
        public string? RawJson { get; set; }

        [JsonIgnore] public ICollection<Sku> Skus { get; set; } = new List<Sku>();
        [JsonIgnore] public ICollection<ProductCategory> ProductCategories { get; set; } = new List<ProductCategory>();
        [JsonIgnore] public ICollection<ProductProperty> Properties { get; set; } = new List<ProductProperty>();
    }
}