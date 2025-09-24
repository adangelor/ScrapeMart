namespace ScrapeMart.Services;

public static class VtexAvailabilityService
{
    /// <summary>
    /// Realiza una prueba de disponibilidad y precio para un SKU en un punto de retiro específico.
    /// </summary>
    /// <returns>Una tupla con (disponibilidad, precio, precio de lista).</returns>
    public static async Task<(bool available, decimal? price, decimal? listPrice)> ProbePickupAsync(
        VtexPublicClient client,
        string skuId,
        string sellerId,
        string? postal,
        (double? lon, double? lat) geo,
        string? salesChannel,
        string pickupPointId,
        CancellationToken ct)
    {
        var salesChannelInt = int.TryParse(salesChannel, out var sc) ? sc : 1;

        // Convertimos el par de nulos a una tupla no nula si ambos valores están presentes
        (double lon, double lat)? geoTuple = geo.lon.HasValue && geo.lat.HasValue
            ? (geo.lon.Value, geo.lat.Value)
            : null;

        try
        {
            var result = await client.SimulatePickupAsync(
                salesChannel: salesChannelInt,
                skuId: skuId,
                quantity: 1,
                sellerId: sellerId,
                countryCode: "AR",
                postalCode: postal,
                geo: geoTuple,
                pickupPointId: pickupPointId,
                ct: ct);

            // Aquí usamos las propiedades con la primera letra en mayúscula
            return (result.Available, result.Price, result.ListPrice);
        }
        catch (VtexHttpException)
        {
            // Si la API de VTEX devuelve un error, asumimos que no está disponible
            return (false, null, null);
        }
        catch (Exception)
        {
            // Otros errores (red, parseo, etc.)
            return (false, null, null);
        }
    }
}