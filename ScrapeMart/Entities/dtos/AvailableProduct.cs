namespace ScrapeMart.Entities.dtos
{
    public sealed class AvailableProduct
    {
        public string EAN { get; set; } = default!;
        public string SkuId { get; set; } = default!;
        public string SellerId { get; set; } = default!;
        public string ProductName { get; set; } = default!;
        public string Owner { get; set; } = default!;
    }

}
