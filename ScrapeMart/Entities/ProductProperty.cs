namespace ScrapeMart.Entities
{
    public sealed class ProductProperty
    {
        public int Id { get; set; }
        public int ProductDbId { get; set; }
        public Product Product { get; set; } = default!;

        public string Name { get; set; } = default!;
        public string Value { get; set; } = default!;
    }
}
