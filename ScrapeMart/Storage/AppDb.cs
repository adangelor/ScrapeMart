using Microsoft.EntityFrameworkCore;
using ScrapeMart.Entities;

namespace ScrapeMart.Storage
{
    public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
        public DbSet<Sku> Skus => Set<Sku>();
        public DbSet<Image> Images => Set<Image>();
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
        public DbSet<Seller> Sellers => Set<Seller>();
        public DbSet<CommercialOffer> Offers => Set<CommercialOffer>();
        public DbSet<ProductProperty> Properties => Set<ProductProperty>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Product>(e =>
            {
                e.HasIndex(x => x.ProductId).IsUnique();
                e.Property(x => x.RawJson).HasColumnType("nvarchar(max)");
            });

            b.Entity<Sku>(e =>
            {
                e.HasIndex(x => x.ItemId).IsUnique();
                e.HasIndex(x => x.Ean);
                e.HasOne(x => x.Product).WithMany(x => x.Skus).HasForeignKey(x => x.ProductDbId);
            });

            b.Entity<Image>(e =>
            {
                e.HasOne(x => x.Sku).WithMany(x => x.Images).HasForeignKey(x => x.SkuDbId);
            });

            b.Entity<ProductCategory>(e =>
            {
                e.HasKey(x => new { x.ProductDbId, x.CategoryDbId });
                e.HasOne(x => x.Product).WithMany(x => x.ProductCategories).HasForeignKey(x => x.ProductDbId);
                e.HasOne(x => x.Category).WithMany(x => x.ProductCategories).HasForeignKey(x => x.CategoryDbId);
            });

            b.Entity<Seller>(e =>
            {
                e.HasIndex(x => new { x.SkuDbId, x.SellerId }).IsUnique();
                e.HasOne(x => x.Sku).WithMany(x => x.Sellers).HasForeignKey(x => x.SkuDbId);
            });

            b.Entity<CommercialOffer>(e =>
            {
                e.HasOne(x => x.Seller).WithMany(x => x.Offers).HasForeignKey(x => x.SellerDbId);
            });

            b.Entity<ProductProperty>(e =>
            {
                e.HasIndex(x => new { x.ProductDbId, x.Name });
                e.HasOne(x => x.Product).WithMany(x => x.Properties).HasForeignKey(x => x.ProductDbId);
            });

            b.Entity<Category>(e =>
            {
                e.HasIndex(x => x.CategoryId).IsUnique();
                e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentDbId);
            });
        }
    }
}
