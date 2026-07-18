using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Ledgers.DTOs;

public record CreateLedgerCreditDto(
    decimal Amount,
    string Description,
    string TransactionId,
    LedgerAccountType AccountType,
    string? BalanceType = null
);

public record LedgerBalanceResponse(
    decimal Available,
    decimal WaitingFunds,
    decimal Disputed,
    decimal Total
);
