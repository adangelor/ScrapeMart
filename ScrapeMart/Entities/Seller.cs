namespace ScrapeMart.Entities
{
    public sealed class Seller
    {
        public int Id { get; set; }
        public int SkuDbId { get; set; }
        public Sku Sku { get; set; } = default!;
        public string SellerId { get; set; } = default!;
        public string? SellerName { get; set; }
        public bool SellerDefault { get; set; }

        public ICollection<CommercialOffer> Offers { get; set; } = new List<CommercialOffer>();
    }
}
