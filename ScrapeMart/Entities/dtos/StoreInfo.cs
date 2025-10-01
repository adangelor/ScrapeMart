namespace ScrapeMart.Entities.dtos
{
    public sealed class StoreInfo
    {
        public long StoreId { get; set; }
        public string StoreName { get; set; } = default!;
        public string City { get; set; } = default!;
        public string Province { get; set; } = default!;
        public string PostalCode { get; set; } = default!;
        public string? VtexPickupPointId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

}
