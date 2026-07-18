using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public record InstallmentReleaseSlot(DateTime ReleaseDate, decimal Amount);

public record PendingInstallmentBatch(Guid TenantId, Guid SellerId, decimal TotalAmount, List<Guid> InstallmentIds);

public interface ITransactionInstallmentRepository
{
    /// <summary>
    /// Persiste um cronograma novo de parcelas (criadas via
    /// <see cref="TransactionInstallment.CreateForTransaction"/>). Não chama SaveChanges;
    /// commit fica a cargo do UnitOfWork do caller.
    /// </summary>
    void AddRange(IEnumerable<TransactionInstallment> installments);

    /// <summary>
    /// Cronograma agregado por dia pra um seller específico. Custo O(log n + k)
    /// com índice filtrado em (TenantId, ExpectedReleaseDate) WHERE Status = PENDING.
    /// </summary>
    Task<List<InstallmentReleaseSlot>> GetReleaseScheduleAsync(
        Guid tenantId, Guid sellerId, DateTime referenceDate, int maxDays = 400);

    /// <summary>
    /// Parcelas PENDING agrupadas por seller que já passaram da data — drive
    /// do SettlementProcessor. Usa JOIN com Transactions pra trazer SellerId
    /// (não denormalizado na installment pra evitar drift).
    /// </summary>
    Task<List<PendingInstallmentBatch>> GetDueForSettlementAsync(DateTime referenceDate);

    /// <summary>
    /// Marca em lote os installments como SETTLED. Usado pelo settlement processor
    /// após mover dinheiro de FUTURE_RECEIVABLES → WALLET. Idempotente: chamar
    /// 2x com mesmos IDs não faz nada (filter STATUS = PENDING).
    /// </summary>
    Task MarkSettledAsync(IEnumerable<Guid> installmentIds, DateTime referenceDate);

    /// <summary>
    /// Pra uma TX específica — usado em refund/dispute pra reverter parcelas pendentes.
    /// </summary>
    Task<List<TransactionInstallment>> GetByTransactionIdAsync(Guid transactionId);

    /// <summary>
    /// Cancela em lote todas as parcelas PENDING de uma TX. Chamado quando a TX é
    /// reembolsada (charge.refunded total) ou perde uma disputa (dispute closed=lost).
    ///
    /// Garantias:
    ///   - Idempotente (filter Status = PENDING; rows já CANCELED/SETTLED ficam intocadas)
    ///   - Não toca parcelas SETTLED — dinheiro já foi pro WALLET, reversão precisa
    ///     passar pelo ledger separadamente (LedgerService.ReversalDebitAsync)
    ///   - Retorna a quantidade de parcelas afetadas, pra log/observabilidade
    /// </summary>
    Task<int> CancelPendingForTransactionAsync(Guid transactionId, DateTime referenceDate);

    Task SaveChangesAsync();
}
