namespace ScrapeMart.Entities.dtos
{
    public sealed class ProductToTrack
    {
        public string EAN { get; set; } = default!;
        public string Owner { get; set; } = default!;
        public string? ProductName { get; set; }
    }

}
