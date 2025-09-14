namespace ScrapeMart.Entities
{
    public sealed class Sku
    {
        public int Id { get; set; }
        public int ProductDbId { get; set; }
        public Product Product { get; set; } = default!;
        public string RetailerHost { get; set; } = default!;
        public string ItemId { get; set; } = default!;
        public string? Name { get; set; }
        public string? NameComplete { get; set; }
        public string? Ean { get; set; }
        public string? MeasurementUnit { get; set; }
        public decimal UnitMultiplier { get; set; }

        public ICollection<Image> Images { get; set; } = new List<Image>();
        public ICollection<Seller> Sellers { get; set; } = new List<Seller>();
    }
}
