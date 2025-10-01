namespace ScrapeMart.Entities.dtos
{
    public sealed class AvailabilityTestResult
    {
        public string ProductEan { get; set; } = default!;
        public string SkuId { get; set; } = default!;
        public string SellerId { get; set; } = default!;
        public bool IsAvailable { get; set; }
        public decimal? Price { get; set; }
        public decimal? ListPrice { get; set; }
        public int AvailableQuantity { get; set; }
        public string Currency { get; set; } = "ARS";
        public int StatusCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RawResponse { get; set; }
    }

}
