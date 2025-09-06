namespace ScrapeMart.Entities
{
    public sealed class ProductCategory
    {
        public int ProductDbId { get; set; }
        public Product Product { get; set; } = default!;
        public int CategoryDbId { get; set; }
        public Category Category { get; set; } = default!;
    }
}
