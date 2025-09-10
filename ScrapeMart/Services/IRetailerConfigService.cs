using Microsoft.EntityFrameworkCore;
using ScrapeMart.Storage;

namespace ScrapeMart.Services
{
    public interface IRetailerConfigService
    {
        Task<RetailerConfig?> GetConfigAsync(string retailerId);
    }

    public sealed class RetailerConfigService(AppDb db) : IRetailerConfigService
    {
        private readonly AppDb _db = db; // Tu DbContext

        public async Task<RetailerConfig?> GetConfigAsync(string retailerId)
        {
            // Aquí leerías tu tabla VtexRetailersConfig
            // Por ahora, lo simulo para no modificar tu DB, pero la idea es reemplazar esto:
            return await _db.VtexRetailersConfigs
                            .Where(cfg => cfg.RetailerId == retailerId && cfg.Enabled)
                            .Select(cfg => new RetailerConfig(
                                cfg.RetailerHost,
                                cfg.SalesChannels.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(int.Parse)
                                                 .ToArray()
                            ))
                            .FirstOrDefaultAsync();
        }
    }

    public sealed record RetailerConfig(string Host, int[] SalesChannels);
}
