using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Transactions.DTOs;

namespace FellowCore.Application.Modules.Transactions.Interfaces;
public interface ITransactionService
{
    Task<TransactionResponseDto> CreateAsync(Guid tenantId, CreateTransactionDto request);
    Task<TransactionDetailDto> GetByIdAsync(Guid tenantId, Guid transactionId);
    Task<PagedResult<TransactionDetailDto>> ListAsync(Guid tenantId, TransactionFilterDto filter);
    Task<RefundResponseDto> RefundAsync(Guid tenantId, Guid transactionId, RefundRequestDto request);
    Task<RefundBreakdownDto> PreviewRefundAsync(Guid tenantId, Guid transactionId, RefundRequestDto request);
    Task CancelAsync(Guid tenantId, Guid transactionId);
    Task<List<RefundDetailDto>> GetRefundsAsync(Guid tenantId, Guid transactionId);
    Task<byte[]> GetReceiptAsync(Guid tenantId, Guid transactionId, string receiptType);
    Task<RefundDetailDto> GetRefundByIdAsync(Guid tenantId, Guid transactionId, string refundCorrelationId);
    Task<TransactionDetailDto> UpdateExpirationAsync(Guid tenantId, Guid transactionId, DateTime expiresAt);
}