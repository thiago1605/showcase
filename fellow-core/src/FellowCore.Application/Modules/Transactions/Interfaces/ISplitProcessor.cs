namespace FellowCore.Application.Modules.Transactions.Interfaces;

public interface ISplitProcessor
{
    Task ProcessSplitsForTransactionAsync(Guid transactionId, CancellationToken ct = default);
    Task ProcessAllPendingSplitsAsync(CancellationToken ct = default);
}
