namespace ScrapeMart.Storage;
public static class MyDbFunctions
{
    // Este es nuestro método fantasma. El código de adentro NUNCA se ejecuta.
    // Solo sirve como un "placeholder" para que EF Core lo reconozca.
    public static string NormalizeHost(string host)
    {
        throw new NotSupportedException("Esta función solo puede ser usada en consultas de EF Core.");
    }
}
