namespace ScrapeMart.Entities
{
    public sealed class Category
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string? Name { get; set; }
        public int? ParentId { get; set; }
        public int? ParentDbId { get; set; }
        public Category? Parent { get; set; }

        public ICollection<Category> Children { get; set; } = new List<Category>();
        public ICollection<ProductCategory> ProductCategories { get; set; } = new List<ProductCategory>();
    }
}
