namespace ScrapeMart.Entities.dtos;

public class ProximitySearchResult
{
    public string RetailerHost { get; set; } = default!;
    public string StoreName { get; set; } = default!;
    public string Address { get; set; } = default!;
    public double DistanceMeters { get; set; }
    public decimal? Price { get; set; }
    public decimal? ListPrice { get; set; }
    public int AvailableQuantity { get; set; }
    public string? FoundPickupPointId { get; set; }
}