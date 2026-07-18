using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Payouts.Services;

/// <summary>
/// Orquestrador do saga de saque multi-provider. Implementa o pattern
/// "compensating transaction" pra garantir consistência mesmo com falha
/// parcial em providers diferentes.
///
/// Algoritmo:
///   1. Allocate amount across providers (greedy, ordem de preferência)
///   2. Persistir <see cref="WithdrawalAttempt"/> + Steps
///   3. Pra cada step (sequencialmente):
///      a. Marcar IN_PROGRESS (incrementa AttemptCount)
///      b. Reservar saldo: debit WALLET (provider-aware) → credit PLATFORM_PAYOUT
///      c. Chamar IPayoutGateway.CreatePayoutAsync (idempotency = step.Id)
///      d. Sucesso → marcar COMPLETED + save ProviderPayoutId
///      e. Falha → marcar FAILED + iniciar compensação
///   4. Compensação:
///      - Pra cada step COMPLETED anterior, tentar cancelar payout no provider
///      - Se cancelar OK → reverter ledger (credit WALLET, debit PLATFORM_PAYOUT)
///      - Se cancelar falhar → step fica em FAILED não-compensado (alerta ops)
///   5. Recompute status do attempt
///
/// Resiliência:
///   - Cada operação é idempotente (idempotency key determinística por step)
///   - Reserva no ledger ANTES do provider previne race
///   - State persistido em DB → resume pode acontecer em outro worker
///   - Provider-level idempotency garante que retry não duplica payout
/// </summary>
public interface IWithdrawOrchestrator
{
    Task<WithdrawalAttempt> ExecuteAsync(Guid tenantId, Guid sellerId, decimal amount, string? idempotencyKey, CancellationToken ct = default);
    /// <summary>Resume um attempt já criado (usado pelo processor pra recuperar saga após crash).</summary>
    Task<WithdrawalAttempt> ResumeAsync(WithdrawalAttempt attempt, CancellationToken ct = default);
}

public class WithdrawOrchestrator(
    IWithdrawalAttemptRepository attemptRepo,
    ISellerRepository sellerRepo,
    ILedgerRepository ledgerRepo,
    IPayoutGatewayFactory gatewayFactory,
    IUnitOfWork unitOfWork,
    ILogger<WithdrawOrchestrator> logger) : IWithdrawOrchestrator
{
    public async Task<WithdrawalAttempt> ExecuteAsync(Guid tenantId, Guid sellerId, decimal amount, string? idempotencyKey, CancellationToken ct = default)
    {
        // Idempotency: se já existe um attempt com a mesma key, retorna ele.
        // Cliente pode fazer POST repetido sem duplicar saque.
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await attemptRepo.GetByIdempotencyKeyAsync(idempotencyKey);
            if (existing != null)
            {
                logger.LogInformation("[WITHDRAW] Idempotency hit pra key {Key} — retornando attempt {AttemptId} ({Status})",
                    idempotencyKey, existing.Id, existing.Status);
                return existing;
            }
        }

        if (amount <= 0)
            throw new BusinessException("Withdraw.InvalidAmount", "Valor do saque deve ser maior que zero.");

        var seller = await sellerRepo.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} não encontrado.");

        // Allocate: pega WALLETs do seller, ordena por preferência (Stripe primeiro),
        // greedy aloca até cobrir amount.
        var wallets = await ledgerRepo.GetSellerAccountsByTypeAsync(tenantId, sellerId, LedgerAccountType.WALLET);
        decimal totalAvailable = wallets.Sum(w => w.Balance);

        if (totalAvailable + 0.001m < amount)
            throw new BusinessException("Withdraw.InsufficientBalance",
                $"Saldo disponível insuficiente: R${totalAvailable:F2} < R${amount:F2}. " +
                "Verifique se há fundos em FUTURE_RECEIVABLES aguardando settlement.");

        // Política de alocação: ordena por preferência do seller (PreferredProvider primeiro),
        // depois pelo maior saldo. Greedy.
        var orderedWallets = wallets
            .Where(w => w.Balance > 0 && w.Provider.HasValue)
            .OrderByDescending(w => w.Provider == seller.PreferredProvider)
            .ThenByDescending(w => w.Balance)
            .ToList();

        var allocations = new List<(PaymentProvider Provider, decimal Amount)>();
        decimal remaining = amount;
        foreach (var w in orderedWallets)
        {
            if (remaining <= 0) break;
            decimal slice = Math.Min(w.Balance, remaining);
            allocations.Add((w.Provider!.Value, slice));
            remaining -= slice;
        }

        // Cria attempt + steps
        var attempt = WithdrawalAttempt.Create(tenantId, sellerId, amount, idempotencyKey);
        for (int i = 0; i < allocations.Count; i++)
        {
            var step = WithdrawalStep.Create(attempt.Id, allocations[i].Provider, allocations[i].Amount, i);
            attempt.AddStep(step);
        }

        attemptRepo.Add(attempt);
        await attemptRepo.SaveChangesAsync();

        logger.LogInformation("[WITHDRAW] Attempt {AttemptId} criado pra seller {SellerId}: R${Amount} em {N} step(s) {Plan}",
            attempt.Id, sellerId, amount, attempt.Steps.Count,
            string.Join(" + ", attempt.Steps.OrderBy(s => s.Sequence).Select(s => $"{s.Provider}:R${s.Amount}")));

        return await ProcessAttemptAsync(attempt, seller, ct);
    }

    public async Task<WithdrawalAttempt> ResumeAsync(WithdrawalAttempt attempt, CancellationToken ct = default)
    {
        var seller = await sellerRepo.GetByIdAsync(attempt.TenantId, attempt.SellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {attempt.SellerId} não encontrado.");

        logger.LogInformation("[WITHDRAW_RESUME] Resumindo attempt {AttemptId} status {Status}",
            attempt.Id, attempt.Status);

        return await ProcessAttemptAsync(attempt, seller, ct);
    }

    /// <summary>
    /// Processa todos os steps PENDING/RESERVED em sequência. Compensação automática
    /// em caso de falha em qualquer step (reverte os que já COMPLETARAM).
    /// </summary>
    private async Task<WithdrawalAttempt> ProcessAttemptAsync(WithdrawalAttempt attempt, Seller seller, CancellationToken ct)
    {
        attempt.MarkInProgress();
        attemptRepo.Update(attempt);
        await attemptRepo.SaveChangesAsync();

        bool needCompensation = false;
        string? failureReason = null;

        foreach (var step in attempt.Steps.OrderBy(s => s.Sequence))
        {
            if (ct.IsCancellationRequested)
            {
                logger.LogWarning("[WITHDRAW] Cancellation requested, parando processing — attempt {AttemptId} fica IN_PROGRESS pra resume",
                    attempt.Id);
                return attempt;
            }

            // Se já concluiu (resume case), pula.
            if (step.Status is WithdrawalStepStatus.COMPLETED or WithdrawalStepStatus.COMPENSATED)
                continue;

            try
            {
                await ProcessStepAsync(attempt, seller, step);
                // ProcessStepAsync pode terminar em COMPLETED (sucesso), FAILED (provider falhou
                // E revert da reserva falhou), ou COMPENSATED (provider falhou + revert OK).
                // Qualquer status != COMPLETED dispara compensação dos steps anteriores.
                if (step.Status != WithdrawalStepStatus.COMPLETED)
                {
                    needCompensation = true;
                    failureReason = $"Step {step.Sequence} ({step.Provider}) não completou ({step.Status}): {step.LastError}";
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WITHDRAW] Exception não tratada em step {StepId} — marcando FAILED + compensando",
                    step.Id);
                step.MarkFailed(ex.Message);
                needCompensation = true;
                failureReason = $"Step {step.Sequence} ({step.Provider}) lançou exceção: {ex.Message}";
                break;
            }
        }

        if (needCompensation)
        {
            await CompensateAsync(attempt, seller);
        }

        attempt.RecomputeStatus(failureReason);
        attemptRepo.Update(attempt);
        await attemptRepo.SaveChangesAsync();

        logger.LogInformation("[WITHDRAW] Attempt {AttemptId} final status: {Status}", attempt.Id, attempt.Status);
        return attempt;
    }

    /// <summary>
    /// Executa um step individual:
    ///   1. Reserva ledger (atomic): debit WALLET (provider), credit PLATFORM_PAYOUT
    ///   2. Chama provider (idempotente)
    ///   3. Marca COMPLETED ou FAILED
    /// </summary>
    private async Task ProcessStepAsync(WithdrawalAttempt attempt, Seller seller, WithdrawalStep step)
    {
        // 1. Reservar saldo (se ainda não reservou)
        if (step.Status == WithdrawalStepStatus.PENDING)
        {
            await ReserveLedgerAsync(attempt, seller, step);
            step.MarkReserved();
            attemptRepo.Update(attempt);
            await attemptRepo.SaveChangesAsync();
        }

        // 2. Chamar provider
        step.MarkInProgress();
        attemptRepo.Update(attempt);
        await attemptRepo.SaveChangesAsync();

        try
        {
            var gateway = gatewayFactory.Get(step.Provider);
            var result = await gateway.CreatePayoutAsync(
                seller, step.Amount, step.IdempotencyKey,
                new Dictionary<string, string>
                {
                    ["attempt_id"] = attempt.Id.ToString(),
                    ["step_id"] = step.Id.ToString(),
                });

            step.MarkCompleted(result.ProviderPayoutId);
            logger.LogInformation("[WITHDRAW] Step {StepId} concluído: {Provider} payout {PayoutId}",
                step.Id, step.Provider, result.ProviderPayoutId);
        }
        catch (Exception ex)
        {
            step.MarkFailed(ex.Message);
            // Reverter a reserva DESTE step (ainda não chamou provider com sucesso).
            // Compensação dos PREVIOUS steps é feita pelo caller.
            await RevertLedgerReservationAsync(attempt, seller, step);
            step.MarkCompensated("reserva revertida pré-execução");
            logger.LogWarning(ex, "[WITHDRAW] Step {StepId} falhou no provider — reserva revertida.",
                step.Id);
        }
        finally
        {
            attemptRepo.Update(attempt);
            await attemptRepo.SaveChangesAsync();
        }
    }

    private async Task ReserveLedgerAsync(WithdrawalAttempt attempt, Seller seller, WithdrawalStep step)
    {
        await unitOfWork.BeginAsync();
        try
        {
            var wallet = await ledgerRepo.GetAccountAsync(attempt.TenantId, LedgerAccountType.WALLET, attempt.SellerId, step.Provider)
                ?? throw new BusinessException("Withdraw.NoWallet",
                    $"Seller {attempt.SellerId} não tem WALLET pra provider {step.Provider}.");

            var debit = wallet.Debit(step.Amount, $"Reserva saque step {step.Id:N}", "WITHDRAW_RESERVE", step.Id.ToString());
            if (debit.IsFailure)
                throw new BusinessException(debit.Error.Code, debit.Error.Description);

            var platformPayout = await ledgerRepo.GetPlatformAccountAsync(attempt.TenantId, LedgerAccountType.PLATFORM_PAYOUT)
                ?? LedgerAccount.Create(attempt.TenantId, null, LedgerAccountType.PLATFORM_PAYOUT);
            if (platformPayout.Id == Guid.Empty || platformPayout.Balance == 0 && !await ledgerRepo.HasEntryWithReferenceAsync(attempt.TenantId, "INIT", "PLATFORM_PAYOUT"))
            {
                // Cria se não existir
                var fresh = await ledgerRepo.GetPlatformAccountAsync(attempt.TenantId, LedgerAccountType.PLATFORM_PAYOUT);
                if (fresh == null)
                {
                    platformPayout = LedgerAccount.Create(attempt.TenantId, null, LedgerAccountType.PLATFORM_PAYOUT);
                    ledgerRepo.AddAccount(platformPayout);
                }
                else
                {
                    platformPayout = fresh;
                }
            }

            var credit = platformPayout.Credit(step.Amount, $"Saque seller via {step.Provider}", "WITHDRAW_RESERVE", step.Id.ToString());
            if (credit.IsFailure)
                throw new BusinessException(credit.Error.Code, credit.Error.Description);

            debit.Value.LinkContraEntry(credit.Value.Id);
            credit.Value.LinkContraEntry(debit.Value.Id);
            ledgerRepo.AddEntry(debit.Value);
            ledgerRepo.AddEntry(credit.Value);

            await ledgerRepo.SaveChangesAsync();
            await unitOfWork.CommitAsync();
        }
        catch
        {
            await unitOfWork.RollbackAsync();
            throw;
        }
    }

    private async Task RevertLedgerReservationAsync(WithdrawalAttempt attempt, Seller seller, WithdrawalStep step)
    {
        await unitOfWork.BeginAsync();
        try
        {
            var wallet = await ledgerRepo.GetAccountAsync(attempt.TenantId, LedgerAccountType.WALLET, attempt.SellerId, step.Provider);
            if (wallet == null)
            {
                logger.LogWarning("[WITHDRAW] Wallet sumiu durante revert do step {StepId}", step.Id);
                await unitOfWork.CommitAsync();
                return;
            }
            var platformPayout = await ledgerRepo.GetPlatformAccountAsync(attempt.TenantId, LedgerAccountType.PLATFORM_PAYOUT);

            var creditBack = wallet.Credit(step.Amount, $"Revert reserva step {step.Id:N}", "WITHDRAW_REVERT", step.Id.ToString());
            if (creditBack.IsFailure) throw new BusinessException(creditBack.Error.Code, creditBack.Error.Description);

            ledgerRepo.AddEntry(creditBack.Value);

            if (platformPayout != null)
            {
                var debitBack = platformPayout.Debit(step.Amount, $"Revert saque step {step.Id:N}", "WITHDRAW_REVERT", step.Id.ToString());
                if (debitBack.IsFailure) throw new BusinessException(debitBack.Error.Code, debitBack.Error.Description);
                creditBack.Value.LinkContraEntry(debitBack.Value.Id);
                debitBack.Value.LinkContraEntry(creditBack.Value.Id);
                ledgerRepo.AddEntry(debitBack.Value);
            }

            await ledgerRepo.SaveChangesAsync();
            await unitOfWork.CommitAsync();
        }
        catch
        {
            await unitOfWork.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Compensação: pra cada step COMPLETED, tentar cancelar payout no provider.
    /// Se cancelar OK → reverter ledger (= creditar WALLET de volta).
    /// Se cancelar falhar → step continua COMPLETED mas attempt fica
    /// PARTIALLY_COMPLETED com FailureSummary indicando intervenção manual.
    /// </summary>
    private async Task CompensateAsync(WithdrawalAttempt attempt, Seller seller)
    {
        var completedSteps = attempt.Steps
            .Where(s => s.Status == WithdrawalStepStatus.COMPLETED)
            .OrderByDescending(s => s.Sequence) // LIFO — reverte o mais recente primeiro
            .ToList();

        foreach (var step in completedSteps)
        {
            try
            {
                var gateway = gatewayFactory.Get(step.Provider);
                bool cancelled = await gateway.TryCancelPayoutAsync(seller, step.ProviderPayoutId!);

                if (cancelled)
                {
                    await RevertLedgerReservationAsync(attempt, seller, step);
                    step.MarkCompensated($"payout {step.ProviderPayoutId} cancelado no provider");
                    logger.LogInformation("[WITHDRAW_COMPENSATE] Step {StepId} compensado.", step.Id);
                }
                else
                {
                    logger.LogCritical(
                        "[WITHDRAW_COMPENSATE] Step {StepId} ({Provider} payout {PayoutId}) NÃO PÔDE ser cancelado. " +
                        "Compensação manual necessária — dinheiro saiu mas outro step falhou.",
                        step.Id, step.Provider, step.ProviderPayoutId);
                    // Step continua COMPLETED — attempt vai pra PARTIALLY_COMPLETED no recompute.
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WITHDRAW_COMPENSATE] Falha tentando compensar step {StepId}", step.Id);
            }
            attemptRepo.Update(attempt);
            await attemptRepo.SaveChangesAsync();
        }
    }
}
