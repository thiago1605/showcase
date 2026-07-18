using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Fiscal;

public interface IFiscalService
{
    Task<SellerFiscalSettings> GetOrCreateSettingsAsync(Guid tenantId, Guid sellerId);
    Task<SellerFiscalSettings> UpdateSettingsAsync(Guid tenantId, Guid sellerId, UpdateFiscalSettingsDto dto);
    Task EnableAsync(Guid tenantId, Guid sellerId);
    Task DisableAsync(Guid tenantId, Guid sellerId);
    Task<FiscalInvoice> RequestInvoiceAsync(Guid tenantId, Guid transactionId);
    Task<FiscalInvoice?> GetInvoiceByTransactionAsync(Guid tenantId, Guid transactionId);
    Task<List<FiscalInvoice>> GetInvoicesBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0);
}

public record UpdateFiscalSettingsDto(
    string? MunicipalRegistration,
    string? ServiceCode,
    decimal IssRate,
    string? CityCode
);
