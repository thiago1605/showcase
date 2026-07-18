using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Transactions.Interfaces;

public record GatewaySubAccountDetails(string ExternalAccountId, string ApiKey, string? PixKey = null);

public interface IPaymentProvider
{
    Task<GatewayPaymentDetails> ProcessPaymentAsync(Tenant tenant, Seller? seller, CreateTransactionDto request, decimal feeAmount, string? idempotencyKey = null, Guid? transactionId = null);
    Task<GatewaySubAccountDetails> CreateSubAccountAsync(Tenant tenant, CreateSellerDto request);
    Task<string?> RefundAsync(Tenant tenant, Seller? seller, string providerTxId, decimal amountInReais, string? reason = null, string? idempotencyKey = null);
    Task CancelChargeAsync(Tenant tenant, Seller? seller, string providerTxId) => throw new NotSupportedException("Cancelamento nao suportado por este provider.");
    Task<AccountBalanceDetails> GetAccountBalanceAsync(Tenant tenant, Seller seller) => throw new NotSupportedException("Consulta de saldo nao suportada por este provider.");
    Task<PixKeyDetails> ValidatePixKeyAsync(Tenant tenant, string pixKey) => throw new NotSupportedException("Validacao de chave Pix nao suportada por este provider.");
    Task<List<StatementEntry>> GetStatementAsync(Tenant tenant, Seller seller, DateTime? start = null, DateTime? end = null) => throw new NotSupportedException("Extrato nao suportado por este provider.");
}

public record AccountBalanceDetails(decimal TotalInReais, decimal BlockedInReais, decimal AvailableInReais, bool IsReady);
public record PixKeyDetails(string Key, string Type, string? OwnerName, string? OwnerDocument);
public record StatementEntry(string? EndToEndId, decimal AmountInReais, string? Time, string? Type, string? Description);
