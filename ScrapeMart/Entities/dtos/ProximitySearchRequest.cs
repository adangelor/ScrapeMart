namespace ScrapeMart.Entities.dtos;

public class ProximitySearchRequest
{
    public string Ean { get; set; } = default!;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int? RadiusMeters { get; set; } = 15000; // Valor por defecto de 15km
}
