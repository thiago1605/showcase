using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ISellerFiscalSettingsRepository
{
    Task<SellerFiscalSettings?> GetBySellerIdAsync(Guid tenantId, Guid sellerId);
    Task AddAsync(SellerFiscalSettings settings);
}
