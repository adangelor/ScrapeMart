namespace ScrapeMart.Entities
{
    public sealed class Image
    {
        public int Id { get; set; }
        public int SkuDbId { get; set; }
        public Sku Sku { get; set; } = default!;

        public string? ImageId { get; set; }
        public string? Label { get; set; }
        public string? Url { get; set; }
        public string? Alt { get; set; }
    }
}
