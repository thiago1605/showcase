using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SellerFiscalSettingsRepository(AppDbContext context) : ISellerFiscalSettingsRepository
{
    public async Task<SellerFiscalSettings?> GetBySellerIdAsync(Guid tenantId, Guid sellerId)
    {
        return await context.SellerFiscalSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SellerId == sellerId);
    }

    public async Task AddAsync(SellerFiscalSettings settings)
    {
        await context.SellerFiscalSettings.AddAsync(settings);
    }
}
