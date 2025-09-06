namespace ScrapeMart.Entities
{
    public sealed class CommercialOffer
    {
        public int Id { get; set; }
        public int SellerDbId { get; set; }
        public Seller Seller { get; set; } = default!;

        public decimal Price { get; set; }
        public decimal ListPrice { get; set; }
        public decimal SpotPrice { get; set; }
        public decimal PriceWithoutDiscount { get; set; }
        public DateTime? PriceValidUntilUtc { get; set; }
        public int AvailableQuantity { get; set; }
        public DateTime CapturedAtUtc { get; set; }
    }
}
