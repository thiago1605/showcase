using FellowCore.Domain.Enums;

namespace FellowCore.Application.Common.Interfaces;

public interface IExportService
{
    Task<byte[]> ExportTransactionsCsvAsync(Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, TransactionStatus? status, PaymentType? paymentType, PaymentProvider? provider);
    Task<byte[]> ExportTransactionsPdfAsync(Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, TransactionStatus? status, PaymentType? paymentType, PaymentProvider? provider);
    Task<byte[]> ExportPayoutsCsvAsync(Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status);
    Task<byte[]> ExportPayoutsPdfAsync(Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status);
}
