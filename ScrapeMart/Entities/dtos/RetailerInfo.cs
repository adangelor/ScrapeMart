namespace ScrapeMart.Entities.dtos
{

    public sealed class RetailerInfo
    {
        public string RetailerId { get; set; } = default!;
        public string DisplayName { get; set; } = default!;
        public string VtexHost { get; set; } = default!;
        public int[] SalesChannels { get; set; } = Array.Empty<int>();
        public int StoreCount { get; set; }
    }

}
