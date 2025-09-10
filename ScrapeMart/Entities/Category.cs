namespace ScrapeMart.Entities
{
    public sealed class Category
    {
        public int Id { get; set; }

        // --- ¡CAMPOS NUEVOS PARA IDENTIFICACIÓN ÚNICA! ---
        public string RetailerHost { get; set; } = default!;
        // --- FIN DE CAMPOS NUEVOS ---

        public int CategoryId { get; set; }
        public string? Name { get; set; }
        public int? ParentId { get; set; }
        public int? ParentDbId { get; set; }
        public Category? Parent { get; set; }

        public ICollection<Category> Children { get; set; } = new List<Category>();
        public ICollection<ProductCategory> ProductCategories { get; set; } = new List<ProductCategory>();
    }
}