using System.Security.Cryptography;
using System.Text;
using FellowCore.Application.Common;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Common.Models;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Webhooks.Services;

public class WebhooksService(
    ITransactionRepository transactionRepository,
    ITransactionInstallmentRepository installmentRepository,
    ISellerRepository sellerRepository,
    ITenantRepository tenantRepository,
    IWebhookEndpointRepository webhookEndpointRepository,
    IWebhookDeliveryRepository webhookDeliveryRepository,
    IInboundWebhookEventRepository inboundWebhookEventRepository,
    ILedgerService ledgerService,
    ISecurityService securityService,
    IConfiguration configuration,
    IUnitOfWork unitOfWork,
    IBackgroundJobs backgroundJobs,
    IRailRouter railRouter,
    IPaymentIntentRepository paymentIntentRepository,
    IDisputeRepository disputeRepository,
    ISplitTransferRepository splitTransferRepository,
    IDomainEventDispatcher domainEventDispatcher,
    IWebhookProbeClient webhookProbeClient,
    IAppMetrics appMetrics,
    FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator advanceRiskEvaluator,
    Microsoft.Extensions.Options.IOptions<FellowCore.Application.Modules.Pricing.Options.TierPricingOptions> tierPricingOptions,
    FellowCore.Application.Modules.Notifications.Interfaces.INotificationService notificationService,
    ILogger<WebhooksService> logger) : IWebhooksService
{
    private readonly FellowCore.Application.Modules.Pricing.Options.TierPricingOptions _tierOpts = tierPricingOptions.Value;

    public async Task HandleStripeEventAsync(StripeWebhookDto payload)
    {
        bool isConnectedAccountEvent = !string.IsNullOrEmpty(payload.Account);
        logger.LogInformation(
            "Webhook Stripe recebido: {Event} | Origem: {Origin} | Account: {Account}",
            payload.Type,
            isConnectedAccountEvent ? "connected_account" : "platform",
            payload.Account ?? "n/a");

        // Handle account.updated for Custom Connected Accounts (KYC status)
        if (payload.Type == "account.updated")
        {
            await HandleStripeAccountUpdatedAsync(payload);
            return;
        }

        // charge.refunded: data.object is a Charge (id=ch_*), not a PaymentIntent
        if (payload.Type == "charge.refunded")
        {
            await HandleStripeChargeRefundedAsync(payload);
            return;
        }

        // charge.dispute.created / charge.dispute.closed
        if (payload.Type is "charge.dispute.created" or "charge.dispute.updated" or "charge.dispute.closed")
        {
            await HandleStripeDisputeAsync(payload);
            return;
        }

        string providerTxId = payload.Data.Object.Id;
        logger.LogInformation("Webhook Stripe processando: {Event} para {TxId}", payload.Type, providerTxId);

        TransactionStatus? newStatus = payload.Type switch
        {
            "payment_intent.succeeded" => TransactionStatus.CAPTURED,
            "payment_intent.payment_failed" => TransactionStatus.DECLINED,
            "payment_intent.canceled" => TransactionStatus.VOIDED,
            _ => null
        };

        if (newStatus == null) return;

        var transaction = await transactionRepository.GetByProviderTxIdAsync(providerTxId);

        if (transaction == null)
        {
            logger.LogWarning("Transacao nao encontrada para ProviderTxId Stripe: {TxId}", providerTxId);
            return;
        }

        if (transaction.Provider != PaymentProvider.STRIPE)
        {
            logger.LogWarning("Webhook Stripe recebido para transacao {Id} que pertence ao provider {Provider}", transaction.Id, transaction.Provider);
            return;
        }

        if (!await ValidateConnectedAccountAsync(payload.Account, transaction))
            return;

        if (transaction.Status == newStatus.Value) return;

        if (!Transaction.IsValidTransition(transaction.Status, newStatus.Value))
        {
            logger.LogWarning("Stripe webhook ignorado: transicao invalida {From} -> {To} para TX {TxId}",
                transaction.Status, newStatus.Value, transaction.Id);
            return;
        }

        await unitOfWork.BeginAsync();
        try
        {
            // Extract wallet type (apple_pay, google_pay, link) from charge data
            string? walletType = null;
            string? chargeId = null;
            if (newStatus == TransactionStatus.CAPTURED)
            {
                var firstCharge = payload.Data.Object.Charges?.Data?.FirstOrDefault();
                walletType = firstCharge?.PaymentMethodDetails?.Card?.Wallet?.Type;
                chargeId = firstCharge?.Id; // ch_xxx — drives StripeAdvanceReconciler
                if (!string.IsNullOrEmpty(walletType))
                {
                    logger.LogInformation("Transacao {Id} paga via wallet: {WalletType}", transaction.Id, walletType);
                }
            }

            // Capture old status before SetStatusAsync (ExecuteUpdateAsync doesn't mutate in-memory entity)
            var oldStatus = transaction.Status;

            // Use ExecuteUpdateAsync to bypass xmin concurrency token — avoids being
            // affected if DetachAllEntities() is called during ledger retry below
            if (!string.IsNullOrEmpty(walletType))
                await transactionRepository.SetStatusWithWalletAsync(transaction.Id, newStatus.Value, walletType);
            else
                await transactionRepository.SetStatusAsync(transaction.Id, newStatus.Value);

            // Persiste Stripe charge ID se veio no webhook — drives reconciler.
            // Idempotente: SetStripeChargeIdAsync ignora se já tem valor.
            if (!string.IsNullOrEmpty(chargeId))
                await transactionRepository.SetStripeChargeIdAsync(transaction.Id, chargeId);

            // Refresh the in-memory tracked entity so any downstream read in the same
            // DbContext (notably SplitProcessor.ProcessSplitsForTransactionAsync, which
            // checks `transaction.Status != CAPTURED` and bails) sees the new value.
            // ExecuteUpdateAsync mutates the DB directly but doesn't update tracker state.
            await transactionRepository.ReloadAsync(transaction);

            if (newStatus == TransactionStatus.CAPTURED && transaction.SellerId.HasValue && transaction.NetAmount.HasValue)
            {
                // PaymentIntent-based collision guard: only first CAPTURED wins
                if (transaction.PaymentIntentId.HasValue)
                {
                    bool won = await paymentIntentRepository.TryCaptureAsync(transaction.PaymentIntentId.Value, transaction.Id);
                    if (!won)
                    {
                        logger.LogCritical(
                            "[COLLISION] PaymentIntent {IntentId} already captured. " +
                            "Late TX {LateTxId} ({LateMethod}) will NOT credit ledger. Auto-refund required.",
                            transaction.PaymentIntentId.Value, transaction.Id, transaction.PaymentType);

                        await unitOfWork.CommitAsync();

                        // Enqueue reconciliation so DOUBLE_CAPTURE issue is created and auto-refund is tracked
                        backgroundJobs.Enqueue<IReconciliationService>(
                            svc => svc.ReconcileTransactionAsync(transaction.TenantId, transaction.Id, CancellationToken.None));

                        return;
                    }
                }
                else if (!string.IsNullOrEmpty(transaction.ExternalReferenceId))
                {
                    // Fallback for transactions created before PaymentIntent migration
                    var existing = await transactionRepository.GetCapturedByExternalReferenceAsync(
                        transaction.TenantId, transaction.ExternalReferenceId, transaction.Id);

                    if (existing != null)
                    {
                        logger.LogCritical(
                            "[COLLISION] Order {OrderId} already paid by TX {ExistingTxId} ({ExistingMethod}). " +
                            "Late payment TX {LateTxId} ({LateMethod}) will NOT credit ledger. Auto-refund required.",
                            transaction.ExternalReferenceId, existing.Id, existing.PaymentType,
                            transaction.Id, transaction.PaymentType);

                        await unitOfWork.CommitAsync();

                        // Enqueue reconciliation so DOUBLE_CAPTURE issue is created and auto-refund is tracked
                        backgroundJobs.Enqueue<IReconciliationService>(
                            svc => svc.ReconcileTransactionAsync(transaction.TenantId, transaction.Id, CancellationToken.None));

                        return;
                    }
                }

                var rail = railRouter.ResolveRailForTransaction(transaction);

                // Determine charge mode from tenant config
                var tenant = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
                var chargeMode = tenant?.Config?.StripeChargeMode ?? StripeChargeMode.DESTINATION_CHARGE;

                // Check for splits — if present, route through SPLIT_CLEARING (unless DIRECT_CHARGE)
                bool hasSplits = await transactionRepository.HasSplitsAsync(transaction.Id);

                if (hasSplits && chargeMode == StripeChargeMode.DIRECT_CHARGE)
                {
                    // CRITICAL: Direct Charge + splits should never happen (blocked at creation time).
                    // If it does, funds are in the connected account — SPLIT_CLEARING is impossible.
                    logger.LogCritical("[LEDGER] CRITICAL: Transaction {TxId} has splits but is DIRECT_CHARGE. Splits cannot be distributed — funds are in connected account. Manual intervention required.", transaction.Id);
                    // Record funds directly to seller since that's where they actually are
                    await ledgerService.RecordDirectChargeFundsAsync(
                        transaction.TenantId,
                        transaction.SellerId.Value,
                        transaction.NetAmount.Value,
                        transaction.FeeAmount ?? 0M,
                        rail.CaptureAccountType,
                        $"Liquidacao Stripe [Direct] Ref: {transaction.Id}");

                    // Prevent SplitProcessor from attempting distribution — SPLIT_CLEARING was never funded
                    var txWithSplits = await transactionRepository.GetByIdWithSplitsAsync(transaction.TenantId, transaction.Id);
                    if (txWithSplits?.Splits.Any(s => s.Status == SplitStatus.PENDING) == true)
                    {
                        foreach (var split in txWithSplits.Splits.Where(s => s.Status == SplitStatus.PENDING))
                            split.MarkAsFailed();
                        logger.LogWarning("[SPLIT] Cancelled {Count} pending splits for DIRECT_CHARGE TX {TxId} — SPLIT_CLEARING never funded",
                            txWithSplits.Splits.Count(s => s.Status == SplitStatus.FAILED), transaction.Id);
                    }
                }
                else if (hasSplits)
                {
                    // Split flow: PLATFORM_RECEIVABLE → SPLIT_CLEARING (held until SplitProcessor distributes)
                    await ledgerService.CreditSplitClearingAsync(
                        transaction.TenantId,
                        transaction.NetAmount.Value,
                        $"Liquidacao Stripe [Split] Ref: {transaction.Id}",
                        transaction.Id.ToString());
                }
                else if (chargeMode == StripeChargeMode.DIRECT_CHARGE)
                {
                    // Direct Charge without splits: money went directly to seller's Stripe connected account
                    await ledgerService.RecordDirectChargeFundsAsync(
                        transaction.TenantId,
                        transaction.SellerId.Value,
                        transaction.NetAmount.Value,
                        transaction.FeeAmount ?? 0M,
                        rail.CaptureAccountType,
                        $"Liquidacao Stripe [Direct] Ref: {transaction.Id}");
                }
                else
                {
                    // Destination Charge without splits: PLATFORM_RECEIVABLE → seller WALLET/FUTURE_RECEIVABLES
                    await ledgerService.RecordIncomingFundsAsync(
                        transaction.TenantId,
                        transaction.SellerId.Value,
                        transaction.NetAmount.Value,
                        rail.CaptureAccountType,
                        $"Liquidacao Stripe Ref: {transaction.Id}");
                }

                // Record platform margin breakdown (fee → provider cost + margin)
                if (transaction.PlatformFeeAmount.HasValue && transaction.PlatformFeeAmount.Value > 0)
                {
                    await ledgerService.RecordPlatformMarginAsync(
                        transaction.TenantId,
                        transaction.PlatformFeeAmount.Value,
                        transaction.ProviderCostAmount ?? 0m,
                        $"Margem TX {transaction.Id}",
                        transaction.Id.ToString());
                }

                // Notificação in-app pro seller (Fase 1 do roadmap de notifs).
                // Producer fire-and-forget — NotificationService.CreateAsync já tem
                // try/catch interno que loga e swallow. Falha aqui NÃO quebra a
                // captura (que é a operação principal e crítica).
                await notificationService.NotifyTransactionCapturedAsync(
                    transaction.TenantId,
                    transaction.SellerId.Value,
                    transaction.Id,
                    transaction.Amount,
                    PaymentMethodLabel(transaction.PaymentType));

                // Cronograma de parcelas: só gera pra TXs que ficam em FUTURE_RECEIVABLES
                // (rails com settlement delay). Modelo Híbrido:
                //   - INSTALLMENT (default): N parcelas mensais pra crédito Nx; 1 parcela
                //     D+2 pra débito; 1 parcela D+30 pra crédito 1x
                //   - ADVANCE (seller.AutoAdvanceSettlement=true E crédito): 1 parcela D+30
                //     com NetAmount - advanceFee. Fee vai pra PLATFORM_MARGIN.
                if (rail.CaptureAccountType == LedgerAccountType.FUTURE_RECEIVABLES
                    && transaction.NetAmount.HasValue
                    && transaction.NetAmount.Value > 0
                    && transaction.SellerId.HasValue)
                {
                    var capturedAt = DateTime.UtcNow;

                    // Decide modo: ADVANCE só pra crédito. Precedência:
                    //   1. transaction.AdvanceOptIn (override per-TX do checkout)
                    //   2. seller.AutoAdvanceSettlement (flag global do seller)
                    //   3. false (default INSTALLMENT)
                    // Débito (D+2) e Boleto/PIX (WALLET direto) ignoram — sem settlement delay.
                    var seller = await sellerRepository.GetByIdAsync(transaction.TenantId, transaction.SellerId.Value);
                    bool sellerWantsAdvance = seller?.AutoAdvanceSettlement ?? false;
                    bool effectiveAdvance = transaction.AdvanceOptIn ?? sellerWantsAdvance;
                    bool eligibleForAdvance = transaction.PaymentType == PaymentType.CREDIT_CARD
                                              && seller != null
                                              && effectiveAdvance;

                    // Anti-fraude: avalia regras de risco antes de antecipar. Fallback
                    // INSTALLMENT silencioso quando bloqueado preserva UX (seller recebe
                    // parcelado em vez de ver erro 422). Telemetria registra a razão.
                    if (eligibleForAdvance)
                    {
                        var riskEval = await advanceRiskEvaluator.EvaluateAsync(transaction, seller!);
                        if (!riskEval.IsEligible)
                        {
                            appMetrics.RecordAdvanceThrottled(riskEval.BlockReason ?? "unknown");
                            logger.LogInformation(
                                "[ADVANCE_THROTTLED] TX {TxId} bloqueada por {Reason} — fallback INSTALLMENT. Signals: {Signals}",
                                transaction.Id, riskEval.BlockReason, string.Join("; ", riskEval.Signals));
                            eligibleForAdvance = false;
                        }
                    }

                    // Reserve de caixa + limite per-seller: checa elegibilidade ANTES
                    // de cobrar fee/criar parcela. Ambos precisam passar; senão fallback.
                    // Computa netAdvancedToSeller (NetAmount - advanceFee) pra checagem precisa.
                    if (eligibleForAdvance && seller != null)
                    {
                        decimal advancePercent = _tierOpts.AdvancePercentFee;
                        decimal advanceFeePreview = Math.Round(transaction.NetAmount!.Value * advancePercent / 100m, 2);
                        decimal netAdvancedPreview = transaction.NetAmount.Value - advanceFeePreview;
                        long netAdvancedCents = (long)Math.Round(netAdvancedPreview * 100m);

                        var tenantConfigForReserve = tenant?.Config;
                        if (tenantConfigForReserve == null || !tenantConfigForReserve.HasAdvanceReserveFor(netAdvancedCents))
                        {
                            appMetrics.RecordAdvanceThrottled("reserve_exhausted");
                            logger.LogWarning(
                                "[ADVANCE_THROTTLED] TX {TxId} — reserve insuficiente. Disponível: {Reserve}, requisitado: {Net}",
                                transaction.Id, tenantConfigForReserve?.PlatformAdvanceReserveCents ?? 0, netAdvancedCents);
                            eligibleForAdvance = false;
                        }
                        else if (!seller.CanIncreaseAdvanceExposure(netAdvancedPreview))
                        {
                            appMetrics.RecordAdvanceThrottled("seller_limit_reached");
                            logger.LogInformation(
                                "[ADVANCE_THROTTLED] TX {TxId} seller {SellerId} — limite excedido. Atual: {Cur}, requisitado: {Net}, teto: {Cap}",
                                transaction.Id, seller.Id, seller.AdvanceExposureCurrent, netAdvancedPreview, seller.AdvanceCreditLimit);
                            eligibleForAdvance = false;
                        }
                    }

                    if (eligibleForAdvance)
                    {
                        // Sprint 1.5: advance fee % vem do TierPricingOptions (global). Sprint 2: per-tier.
                        decimal advancePercent = _tierOpts.AdvancePercentFee;
                        decimal advanceFee = Math.Round(transaction.NetAmount.Value * advancePercent / 100m, 2);

                        var markResult = transaction.MarkAsAdvanceSettlement(advanceFee);
                        if (markResult.IsFailure)
                            throw new BusinessException(markResult.Error.Code, markResult.Error.Description);

                        // Persiste a mutação no Transaction (SettlementMode + AdvanceFeeAmount)
                        // antes do installment generation pra que CreateForTransaction veja
                        // o estado correto.
                        transactionRepository.EnsureNewTimelineEventsAdded(transaction);
                        await transactionRepository.SaveChangesAsync();

                        // Reserve de caixa: debita tenant + aumenta seller exposure.
                        // As checagens HasAdvanceReserveFor/CanIncreaseExposure já rolaram acima,
                        // mas chamadas falham defensivamente em race conditions.
                        decimal netAdvancedToSeller = transaction.NetAmount!.Value - advanceFee;
                        long netAdvancedCents = (long)Math.Round(netAdvancedToSeller * 100m);
                        var reserveDebit = tenant!.Config!.DebitAdvanceReserve(netAdvancedCents);
                        if (reserveDebit.IsFailure)
                            throw new BusinessException(reserveDebit.Error.Code, reserveDebit.Error.Description);
                        var exposureUp = seller.IncreaseAdvanceExposure(netAdvancedToSeller);
                        if (exposureUp.IsFailure)
                            throw new BusinessException(exposureUp.Error.Code, exposureUp.Error.Description);
                        sellerRepository.Update(seller);
                        await sellerRepository.SaveChangesAsync();

                        // Debita o fee de FUTURE_RECEIVABLES e credita em PLATFORM_MARGIN.
                        if (advanceFee > 0)
                        {
                            await ledgerService.ChargeAdvanceFeeAsync(
                                transaction.TenantId, transaction.SellerId.Value, advanceFee,
                                $"Advance fee TX {transaction.Id} ({advancePercent}% sobre net R${transaction.NetAmount.Value})",
                                transaction.Id.ToString());
                        }
                        else
                        {
                            logger.LogWarning(
                                "[ADVANCE] TX {TxId} em modo ADVANCE com fee=0 — plataforma absorve o custo de adiantamento. " +
                                "Configure TierPricing:AdvancePercentFee pra cobrar.",
                                transaction.Id);
                        }

                        // Gera 1 única parcela D+30 (CreateForTransaction detecta SettlementMode)
                        var advanceInstallments = TransactionInstallment.CreateForTransaction(transaction, capturedAt);
                        installmentRepository.AddRange(advanceInstallments);
                        await installmentRepository.SaveChangesAsync();

                        // Telemetria: aumenta exposure de caixa + receita.
                        appMetrics.RecordAdvanceCapture(advanceInstallments[0].NetAmount, advanceFee);

                        logger.LogInformation(
                            "[ADVANCE] TX {TxId} antecipada: seller recebe R${SellerNet} em D+30 (fee R${Fee} pra plataforma)",
                            transaction.Id, advanceInstallments[0].NetAmount, advanceFee);
                    }
                    else
                    {
                        // Modo INSTALLMENT clássico.
                        int daysBetween = transaction.PaymentType == PaymentType.DEBIT_CARD ? 2 : 30;
                        var installments = TransactionInstallment.CreateForTransaction(
                            transaction, capturedAt, daysBetween);
                        installmentRepository.AddRange(installments);
                        await installmentRepository.SaveChangesAsync();
                        logger.LogInformation(
                            "Cronograma criado pra TX {TxId}: {N} parcela(s) de {PaymentType} (intervalo {Days}d)",
                            transaction.Id, installments.Count, transaction.PaymentType, daysBetween);
                    }
                }
            }

            await unitOfWork.CommitAsync();

            // M5: Dispatch domain event manually since SetStatusAsync bypasses ChangeTracker
            try
            {
                await domainEventDispatcher.DispatchAsync([new TransactionStatusChangedEvent(
                    transaction.Id, transaction.TenantId, oldStatus, newStatus.Value,
                    transaction.SellerId, transaction.NetAmount, transaction.PaymentType, transaction.ProviderTxId)]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao despachar TransactionStatusChangedEvent para TX {TxId} (Stripe). Evento nao-critico.", transaction.Id);
            }

            logger.LogInformation("Transacao {Id} atualizada para {Status} via Stripe", transaction.Id, newStatus);

            // Event-driven reconciliation (fire-and-forget via Hangfire)
            if (newStatus == TransactionStatus.CAPTURED && transaction.Provider == PaymentProvider.STRIPE)
            {
                backgroundJobs.Enqueue<IReconciliationService>(
                    svc => svc.ReconcileTransactionAsync(transaction.TenantId, transaction.Id, CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            await unitOfWork.RollbackAsync();
            logger.LogError(ex, "Erro ao processar Webhook Stripe para {TxId}", providerTxId);
            throw;
        }
    }

    private async Task HandleStripeAccountUpdatedAsync(StripeWebhookDto payload)
    {
        string accountId = payload.Data.Object.Id;
        logger.LogInformation("Stripe account.updated recebido para {AccountId}", accountId);

        var seller = await sellerRepository.GetByExternalAccountIdAsync(accountId);
        if (seller == null)
        {
            logger.LogWarning("Seller nao encontrado para Stripe AccountId: {AccountId}", accountId);
            return;
        }

        bool chargesEnabled = payload.Data.Object.ChargesEnabled == true;
        bool payoutsEnabled = payload.Data.Object.PayoutsEnabled == true;
        string? disabledReason = payload.Data.Object.Requirements?.DisabledReason;

        if (chargesEnabled && payoutsEnabled && seller.Status != SellerStatus.ACTIVE)
        {
            seller.Activate();
            sellerRepository.Update(seller);
            await sellerRepository.SaveChangesAsync();
            logger.LogInformation("Seller {SellerId} ativado via Stripe KYC. Account: {AccountId}", seller.Id, accountId);
        }
        else if (!string.IsNullOrEmpty(disabledReason) && seller.Status == SellerStatus.ACTIVE)
        {
            seller.Suspend();
            sellerRepository.Update(seller);
            await sellerRepository.SaveChangesAsync();
            logger.LogWarning("Seller {SellerId} suspenso. Motivo: {Reason}. Account: {AccountId}", seller.Id, disabledReason, accountId);
        }
    }

    private async Task HandleStripeChargeRefundedAsync(StripeWebhookDto payload)
    {
        // For charge.refunded, data.object.id is the Charge ID (ch_*).
        // We need data.object.payment_intent to find the Transaction.
        string? paymentIntentId = payload.Data.Object.PaymentIntent;
        if (string.IsNullOrEmpty(paymentIntentId))
        {
            logger.LogWarning("charge.refunded recebido sem payment_intent. ChargeId: {ChargeId}", payload.Data.Object.Id);
            return;
        }

        var transaction = await transactionRepository.GetByProviderTxIdAsync(paymentIntentId);
        if (transaction == null)
        {
            logger.LogWarning("Transacao nao encontrada para PaymentIntent Stripe: {TxId}", paymentIntentId);
            return;
        }

        if (transaction.Provider != PaymentProvider.STRIPE)
        {
            logger.LogWarning("Webhook Stripe charge.refunded para transacao {Id} que pertence ao provider {Provider}", transaction.Id, transaction.Provider);
            return;
        }

        if (!await ValidateConnectedAccountAsync(payload.Account, transaction))
            return;

        // amount_refunded is the total refunded amount in cents for this charge
        long amountRefundedCents = payload.Data.Object.AmountRefunded ?? 0;
        decimal amountRefunded = amountRefundedCents / 100m;

        if (amountRefunded <= 0) return;

        await unitOfWork.BeginAsync();
        try
        {
            // Calculate net refund delta (avoid double-processing idempotent webhooks)
            decimal newRefundDelta = amountRefunded - transaction.RefundedAmount;
            if (newRefundDelta <= 0)
            {
                await unitOfWork.RollbackAsync();
                return;
            }

            // Use ExecuteUpdateAsync to apply refund atomically — avoids xmin corruption
            // from calling Update() on tracked entities
            var newStatus = amountRefunded >= transaction.Amount
                ? TransactionStatus.REFUNDED
                : transaction.Status;

            await transactionRepository.ApplyRefundAsync(transaction.Id, amountRefunded, newStatus);

            // Debit seller: GROSS INTEGRAL (política unificada com portal e retry).
            // Mesmo RefundCalculator usado nos outros caminhos pra evitar drift.
            if (transaction.SellerId.HasValue)
            {
                var breakdown = Application.Modules.Transactions.Services.RefundCalculator
                    .Calculate(transaction, newRefundDelta);

                // Splits: o débito do seller acontece via ReverseSplitsProportionallyAsync.
                var activeSplitTransfers = await splitTransferRepository.GetByTransactionIdAsync(transaction.TenantId, transaction.Id);
                decimal totalSplitRemaining = activeSplitTransfers
                    .Where(t => t.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PAID or SplitTransferStatus.PARTIALLY_REVERSED)
                    .Sum(t => t.RemainingAmount);

                if (totalSplitRemaining == 0 && breakdown.SellerTotalDebit > 0)
                {
                    // No splits — debit primary seller direto pelo gross.
                    await ledgerService.DebitSellerAsync(
                        transaction.TenantId,
                        transaction.SellerId.Value,
                        breakdown.SellerTotalDebit,
                        $"Refund Stripe — TX {transaction.Id} (delta: R${newRefundDelta}, gross)",
                        transaction.Id.ToString());
                }

                // L2: DIRECT_CHARGE precisa reverter a platform fee no ledger
                // específico — diferente de reverter margem, isso ajusta o
                // movimento do PaymentIntent quando o seller recebe direto.
                var tenant = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
                if (tenant?.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE)
                {
                    decimal proportionalFee = breakdown.PlatformFeeWithheld;
                    if (proportionalFee > 0)
                    {
                        await ledgerService.ReversePlatformFeeAsync(
                            transaction.TenantId,
                            proportionalFee,
                            $"Estorno fee refund — TX {transaction.Id} (delta: R${newRefundDelta})",
                            transaction.Id.ToString());
                    }
                }
            }

            // NOTA: Removemos a reversão da margem (que existia aqui antes) pra
            // alinhar com a política gross. A margem fica com a plataforma —
            // necessária pra cobrir o custo do provider (Stripe não devolve a
            // taxa deles). Veja RefundCalculator.cs.

            // Reverse splits proportionally for each recipient
            await ReverseSplitsProportionallyAsync(transaction, newRefundDelta);

            // Refund total → cancela parcelas PENDING dessa TX (mesma lógica do
            // TransactionService.RefundAsync). Sem isso, parcelas viraria settlement
            // mesmo com a TX já refunded — virariam dinheiro fantasma no WALLET.
            if (newStatus == TransactionStatus.REFUNDED)
            {
                var canceledCount = await installmentRepository.CancelPendingForTransactionAsync(
                    transaction.Id, DateTime.UtcNow);
                if (canceledCount > 0)
                {
                    logger.LogInformation(
                        "[REFUND] {Count} parcelas PENDING canceladas pra TX {Id} via webhook charge.refunded",
                        canceledCount, transaction.Id);
                }

                // Reverter advance fee em modo ADVANCE (mesma lógica do TransactionService).
                if (transaction.SettlementMode == SettlementMode.ADVANCE
                    && transaction.AdvanceFeeAmount.HasValue
                    && transaction.AdvanceFeeAmount.Value > 0
                    && transaction.SellerId.HasValue)
                {
                    try
                    {
                        await ledgerService.ReverseAdvanceFeeAsync(
                            transaction.TenantId, transaction.SellerId.Value, transaction.AdvanceFeeAmount.Value,
                            $"Reversão advance fee TX {transaction.Id} (refund via webhook)",
                            transaction.Id.ToString());
                        appMetrics.RecordAdvanceReversal();
                        // Devolve reserve + reduz seller exposure proporcionalmente
                        // ao netAdvancedToSeller original (= NetAmount - AdvanceFee).
                        decimal netAdvancedOriginal = transaction.NetAmount!.Value - transaction.AdvanceFeeAmount.Value;
                        long netAdvancedCents = (long)Math.Round(netAdvancedOriginal * 100m);
                        var tenantForReverse = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
                        tenantForReverse?.Config?.CreditAdvanceReserve(netAdvancedCents);
                        var sellerForReverse = await sellerRepository.GetByIdAsync(transaction.TenantId, transaction.SellerId.Value);
                        if (sellerForReverse != null)
                        {
                            sellerForReverse.DecreaseAdvanceExposure(netAdvancedOriginal);
                            sellerRepository.Update(sellerForReverse);
                            await sellerRepository.SaveChangesAsync();
                        }
                        logger.LogInformation(
                            "[REFUND] Advance fee R${Fee} revertido via webhook pra TX {Id} — reserve +R${Cents}c",
                            transaction.AdvanceFeeAmount.Value, transaction.Id, netAdvancedCents);
                    }
                    catch (Exception ex)
                    {
                        logger.LogCritical(ex,
                            "[REFUND] CRITICAL: Falha ao reverter advance fee TX {Id} via webhook. Seller cobrado em duplicidade.",
                            transaction.Id);
                    }
                }
            }

            // In-app notification — antes do commit pra ir junto na mesma TX.
            if (transaction.SellerId.HasValue && newRefundDelta > 0)
            {
                await notificationService.NotifyRefundCompletedAsync(
                    transaction.TenantId, transaction.SellerId.Value,
                    transaction.Id, newRefundDelta);
            }

            await unitOfWork.CommitAsync();

            logger.LogInformation("Transacao {Id} refund atualizado: R${RefundedAmount} via Stripe charge.refunded. Ledger debitado.",
                transaction.Id, amountRefunded);

            // Event-driven reconciliation after refund
            backgroundJobs.Enqueue<IReconciliationService>(
                svc => svc.ReconcileTransactionAsync(transaction.TenantId, transaction.Id, CancellationToken.None));
        }
        catch (Exception ex)
        {
            await unitOfWork.RollbackAsync();
            logger.LogError(ex, "Erro ao processar charge.refunded para PaymentIntent {TxId}", paymentIntentId);
            throw;
        }
    }

    private async Task HandleStripeDisputeAsync(StripeWebhookDto payload)
    {
        string externalDisputeId = payload.Data.Object.Id;
        string? paymentIntentId = payload.Data.Object.PaymentIntent;
        string? stripeDisputeStatus = payload.Data.Object.Status;
        long amountCents = payload.Data.Object.Amount ?? 0;
        decimal amount = amountCents / 100m;

        logger.LogInformation("Stripe dispute {Event} recebido. DisputeId: {DisputeId}, PI: {PaymentIntent}, Amount: {Amount}, Status: {Status}",
            payload.Type, externalDisputeId, paymentIntentId, amount, stripeDisputeStatus);

        if (string.IsNullOrEmpty(paymentIntentId))
        {
            logger.LogWarning("Dispute webhook sem payment_intent. DisputeId: {DisputeId}", externalDisputeId);
            return;
        }

        var transaction = await transactionRepository.GetByProviderTxIdAsync(paymentIntentId);
        if (transaction == null)
        {
            logger.LogWarning("Transacao nao encontrada para PaymentIntent {PI} no dispute {DisputeId}", paymentIntentId, externalDisputeId);
            return;
        }

        if (transaction.Provider != PaymentProvider.STRIPE) return;

        if (!await ValidateConnectedAccountAsync(payload.Account, transaction))
            return;

        decimal holdAmount = transaction.NetAmount ?? amount;

        if (payload.Type == "charge.dispute.created")
        {
            // Idempotent: check if Dispute entity already exists
            var existingDispute = await disputeRepository.GetByExternalIdAsync(externalDisputeId);
            if (existingDispute != null) return;

            if (transaction.Status == TransactionStatus.CHARGEBACKERROR) return;

            // Create Dispute aggregate
            var dispute = Dispute.Create(
                transaction.TenantId, transaction.Id, transaction.SellerId,
                externalDisputeId, holdAmount, stripeDisputeStatus);
            disputeRepository.Add(dispute);

            var disputeOldStatus = transaction.Status;
            await transactionRepository.SetStatusAsync(transaction.Id, TransactionStatus.CHARGEBACKERROR);

            // Update PaymentIntent if linked
            if (transaction.PaymentIntentId.HasValue)
            {
                var intent = await paymentIntentRepository.GetByIdAsync(transaction.TenantId, transaction.PaymentIntentId.Value);
                intent?.MarkDisputed();
            }

            if (transaction.SellerId.HasValue && holdAmount > 0)
            {
                try
                {
                    await ledgerService.HoldDisputeAsync(
                        transaction.TenantId, transaction.SellerId.Value, holdAmount,
                        $"Disputa Stripe — TX {transaction.Id} (Dispute: {externalDisputeId})",
                        transaction.Id.ToString());

                    // L11: For Direct Charge, also freeze the platform fee during dispute
                    var tenantForHold = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
                    if (tenantForHold?.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE
                        && transaction.NetAmount.HasValue && transaction.Amount > 0)
                    {
                        decimal feeAmount = transaction.Amount - transaction.NetAmount.Value;
                        if (feeAmount > 0)
                        {
                            await ledgerService.HoldDisputeFeeAsync(
                                transaction.TenantId, feeAmount,
                                $"Fee congelada disputa — TX {transaction.Id} (Dispute: {externalDisputeId})",
                                transaction.Id.ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falha ao bloquear fundos para disputa {DisputeId}. TX: {TxId}", externalDisputeId, transaction.Id);
                }
            }

            await disputeRepository.SaveChangesAsync();

            // M5: Dispatch domain event manually since SetStatusAsync bypasses ChangeTracker
            try
            {
                await domainEventDispatcher.DispatchAsync([new TransactionStatusChangedEvent(
                    transaction.Id, transaction.TenantId, disputeOldStatus, TransactionStatus.CHARGEBACKERROR,
                    transaction.SellerId, transaction.NetAmount, transaction.PaymentType, transaction.ProviderTxId)]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao despachar TransactionStatusChangedEvent para TX {TxId} (dispute created). Evento nao-critico.", transaction.Id);
            }

            // Producer in-app — enfileira no outbox dentro da mesma transaction.
            if (transaction.SellerId.HasValue)
            {
                await notificationService.NotifyDisputeOpenedAsync(
                    transaction.TenantId,
                    transaction.SellerId.Value,
                    transaction.Id,
                    holdAmount,
                    reason: null /* Stripe reason fica em dispute.Reason mas não temos aqui */);
            }

            logger.LogWarning("Disputa aberta para transacao {TxId}. DisputeId: {DisputeId}. Valor: {Amount}",
                transaction.Id, externalDisputeId, amount);
        }
        else if (payload.Type == "charge.dispute.closed")
        {
            var dispute = await disputeRepository.GetByExternalIdAsync(externalDisputeId);

            if (stripeDisputeStatus == "won")
            {
                dispute?.Win();

                if (transaction.SellerId.HasValue && holdAmount > 0)
                {
                    try
                    {
                        await ledgerService.ReleaseDisputeAsync(
                            transaction.TenantId, transaction.SellerId.Value, holdAmount,
                            $"Disputa vencida — TX {transaction.Id} (Dispute: {externalDisputeId})",
                            transaction.Id.ToString());

                        // L11: For Direct Charge, release frozen platform fee
                        var tenantForRelease = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
                        if (tenantForRelease?.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE
                            && transaction.NetAmount.HasValue && transaction.Amount > 0)
                        {
                            decimal feeAmount = transaction.Amount - transaction.NetAmount.Value;
                            if (feeAmount > 0)
                            {
                                await ledgerService.ReleaseDisputeFeeAsync(
                                    transaction.TenantId, feeAmount,
                                    $"Fee liberada disputa vencida — TX {transaction.Id} (Dispute: {externalDisputeId})",
                                    transaction.Id.ToString());
                            }
                        }

                        await transactionRepository.SetStatusAsync(transaction.Id, TransactionStatus.CAPTURED);

                        // M5: Dispatch domain event manually since SetStatusAsync bypasses ChangeTracker
                        try
                        {
                            await domainEventDispatcher.DispatchAsync([new TransactionStatusChangedEvent(
                                transaction.Id, transaction.TenantId, TransactionStatus.CHARGEBACKERROR, TransactionStatus.CAPTURED,
                                transaction.SellerId, transaction.NetAmount, transaction.PaymentType, transaction.ProviderTxId)]);
                        }
                        catch (Exception evtEx)
                        {
                            logger.LogWarning(evtEx, "Falha ao despachar TransactionStatusChangedEvent para TX {TxId} (dispute won). Evento nao-critico.", transaction.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Falha ao liberar fundos da disputa {DisputeId}. TX: {TxId}", externalDisputeId, transaction.Id);
                    }
                }

                // Producer in-app — venceu (verde).
                if (transaction.SellerId.HasValue)
                {
                    await notificationService.NotifyDisputeResolvedAsync(
                        transaction.TenantId, transaction.SellerId.Value,
                        transaction.Id, holdAmount, won: true);
                }

                logger.LogInformation("Disputa vencida para transacao {TxId}. Fundos liberados.", transaction.Id);
            }
            else if (stripeDisputeStatus == "lost")
            {
                dispute?.Lose();

                // H12: Zero out the DISPUTE account — funds are permanently lost to the cardholder.
                // Debit DISPUTE → credit PLATFORM_PAYOUT to record the chargeback outflow.
                if (transaction.SellerId.HasValue && holdAmount > 0)
                {
                    try
                    {
                        await ledgerService.SettleDisputeLossAsync(
                            transaction.TenantId, transaction.SellerId.Value, holdAmount,
                            $"Disputa perdida — TX {transaction.Id} (Dispute: {externalDisputeId})",
                            transaction.Id.ToString());
                        // L11: For direct charge, settle the frozen dispute fee (DISPUTE_FEE → PLATFORM_PAYOUT)
                        var tenant = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
                        if (tenant?.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE
                            && transaction.NetAmount.HasValue && transaction.Amount > 0)
                        {
                            decimal feeAmount = transaction.Amount - transaction.NetAmount.Value;
                            if (feeAmount > 0)
                            {
                                await ledgerService.SettleDisputeFeeLossAsync(
                                    transaction.TenantId, feeAmount,
                                    $"Fee disputa perdida — TX {transaction.Id} (Dispute: {externalDisputeId})",
                                    transaction.Id.ToString());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Falha ao debitar DISPUTE para disputa perdida {DisputeId}. TX: {TxId}. Intervencao manual necessaria.",
                            externalDisputeId, transaction.Id);
                    }
                }

                // Chargeback (dispute lost) = TX revertida integralmente.
                // Cancela parcelas PENDING — não devem virar settlement já que o
                // dinheiro foi pro cardholder. Parcelas SETTLED não tocadas;
                // SettleDisputeLossAsync acima já debitou DISPUTE no ledger.
                try
                {
                    var canceledCount = await installmentRepository.CancelPendingForTransactionAsync(
                        transaction.Id, DateTime.UtcNow);
                    if (canceledCount > 0)
                    {
                        logger.LogInformation(
                            "[DISPUTE_LOST] {Count} parcelas PENDING canceladas pra TX {Id} (Dispute: {DisputeId})",
                            canceledCount, transaction.Id, externalDisputeId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Falha ao cancelar parcelas pendentes da TX {TxId} na disputa perdida {DisputeId}. Intervenção manual.",
                        transaction.Id, externalDisputeId);
                }

                // Reverter advance fee se TX era ADVANCE — seller perde o gross via
                // SettleDisputeLossAsync acima, mas o fee da antecipação foi cobrado
                // adicionalmente na captura e precisa voltar pra evitar double-charge.
                if (transaction.SettlementMode == SettlementMode.ADVANCE
                    && transaction.AdvanceFeeAmount.HasValue
                    && transaction.AdvanceFeeAmount.Value > 0)
                {
                    try
                    {
                        await ledgerService.ReverseAdvanceFeeAsync(
                            transaction.TenantId, transaction.SellerId.Value, transaction.AdvanceFeeAmount.Value,
                            $"Reversão advance fee TX {transaction.Id} (dispute lost {externalDisputeId})",
                            transaction.Id.ToString());
                        appMetrics.RecordAdvanceReversal();
                        // Reserve + exposure cleanup (mesma lógica do refund)
                        decimal netAdvancedOriginal = transaction.NetAmount!.Value - transaction.AdvanceFeeAmount.Value;
                        long netAdvancedCents = (long)Math.Round(netAdvancedOriginal * 100m);
                        var tenantForReverse = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
                        tenantForReverse?.Config?.CreditAdvanceReserve(netAdvancedCents);
                        var sellerForReverse = await sellerRepository.GetByIdAsync(transaction.TenantId, transaction.SellerId.Value);
                        if (sellerForReverse != null)
                        {
                            sellerForReverse.DecreaseAdvanceExposure(netAdvancedOriginal);
                            sellerRepository.Update(sellerForReverse);
                            await sellerRepository.SaveChangesAsync();
                        }
                        logger.LogInformation(
                            "[DISPUTE_LOST] Advance fee R${Fee} revertido pra TX {Id} — reserve +R${Cents}c",
                            transaction.AdvanceFeeAmount.Value, transaction.Id, netAdvancedCents);
                    }
                    catch (Exception ex)
                    {
                        logger.LogCritical(ex,
                            "[DISPUTE_LOST] CRITICAL: Falha ao reverter advance fee TX {Id} em dispute {DisputeId}. Intervenção manual.",
                            transaction.Id, externalDisputeId);
                    }
                }

                // Reverse splits proportionally (chargeback = full amount loss)
                await ReverseSplitsProportionallyAsync(transaction, transaction.Amount);

                // Reverse platform margin (platform loses the fee on chargeback)
                if (transaction.PlatformFeeAmount.HasValue && transaction.PlatformFeeAmount.Value > 0)
                {
                    try
                    {
                        await ledgerService.ReversePlatformMarginAsync(
                            transaction.TenantId,
                            transaction.PlatformFeeAmount.Value,
                            transaction.ProviderCostAmount ?? 0m,
                            $"Dispute loss margin reversal — TX {transaction.Id} (Dispute: {externalDisputeId})",
                            transaction.Id.ToString());
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Falha ao reverter margem na disputa perdida {DisputeId}. TX: {TxId}", externalDisputeId, transaction.Id);
                    }
                }

                logger.LogWarning("Disputa perdida para transacao {TxId}. Fundos debitados da conta DISPUTE. Margem revertida.", transaction.Id);
            }

            if (dispute != null)
            {
                disputeRepository.Update(dispute);
                await disputeRepository.SaveChangesAsync();

                // Producer in-app — perdeu (vermelho).
                if (transaction.SellerId.HasValue)
                {
                    await notificationService.NotifyDisputeResolvedAsync(
                        transaction.TenantId, transaction.SellerId.Value,
                        transaction.Id, holdAmount, won: false);
                }
            }
        }
    }

    /// <summary>
    /// Validates that a Stripe Connect webhook's account field matches the seller's ExternalAccountId.
    /// For DirectCharge events, payload.Account must match. For platform events, payload.Account is null.
    /// </summary>
    private async Task<bool> ValidateConnectedAccountAsync(string? webhookAccount, Transaction transaction)
    {
        if (string.IsNullOrEmpty(webhookAccount))
        {
            // For DirectCharge tenants, events MUST come as Connect events with account field
            var tenant = await tenantRepository.GetByIdWithConfigAsync(transaction.TenantId);
            if (tenant?.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE && transaction.SellerId.HasValue)
            {
                logger.LogWarning(
                    "[WEBHOOK] DirectCharge tenant but webhook has no account field. TX: {TxId}. Rejecting — expected Connect event.",
                    transaction.Id);
                return false;
            }
            return true; // Platform/DestinationCharge event — no connected account to validate
        }

        if (!transaction.SellerId.HasValue)
        {
            logger.LogWarning(
                "[WEBHOOK] Stripe Connect event from account {Account} but TX {TxId} has no seller.",
                webhookAccount, transaction.Id);
            return false;
        }

        var seller = await sellerRepository.GetByIdAsync(transaction.TenantId, transaction.SellerId.Value);
        if (seller == null)
        {
            logger.LogWarning(
                "[WEBHOOK] Seller {SellerId} not found for TX {TxId}. Connected account: {Account}",
                transaction.SellerId.Value, transaction.Id, webhookAccount);
            return false;
        }

        if (!string.Equals(seller.ExternalAccountId, webhookAccount, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "[WEBHOOK] Account mismatch! Webhook account {WebhookAccount} != seller {SellerId} ExternalAccountId {SellerAccount}. TX: {TxId}. Ignoring event.",
                webhookAccount, seller.Id, seller.ExternalAccountId, transaction.Id);
            return false;
        }

        return true;
    }

    public async Task HandleOpenPixEventAsync(OpenPixWebhookDto payload, string? authToken = null)
    {
        logger.LogInformation("Webhook OpenPix recebido: {Event}", payload.Event);

        // Idempotência inbound: Woovi pode retentar a entrega quando não recebe 2xx
        // rápido. Dedup pela chave composta (event_type:correlationID). Para eventos
        // ACCOUNT_REGISTER_* sem charge, usa o correlationID do bloco accountRegister.
        var correlationForDedup = payload.Charge?.CorrelationId
            ?? payload.AccountRegister?.CorrelationId
            ?? Guid.NewGuid().ToString(); // sem chave clara, gera GUID — efetivamente bypassa dedup pro evento
        var eventIdForDedup = $"{payload.Event}:{correlationForDedup}";

        var inbound = await inboundWebhookEventRepository.TryRegisterReceivedAsync(
            PaymentProvider.OPENPIX, eventIdForDedup, payload.Event);

        if (inbound is null)
        {
            // Duplicado — outro processo já registrou. Retorna OK silenciosamente.
            logger.LogInformation("[INBOUND_WEBHOOK] OpenPix duplicado, dedup ativo. Event: {Event} | Ref: {Ref}",
                payload.Event, correlationForDedup);
            return;
        }

        try
        {
            await ProcessOpenPixEventInternalAsync(payload, authToken);
            await inboundWebhookEventRepository.MarkProcessedAsync(inbound.Id);
        }
        catch (Exception ex)
        {
            // Mantém o registro com `Processed=false` + erro pra próximo delivery
            // do mesmo event tentar de novo (Woovi retenta automático em falha).
            await inboundWebhookEventRepository.MarkFailedAsync(inbound.Id, ex.Message);
            throw;
        }
    }

    private async Task ProcessOpenPixEventInternalAsync(OpenPixWebhookDto payload, string? authToken)
    {
        if (payload.Event is "ACCOUNT_REGISTER_APPROVED" or "ACCOUNT_REGISTER_REJECTED" or "ACCOUNT_REGISTER_PENDING")
        {
            await HandleAccountRegisterEventAsync(payload, authToken);
            return;
        }

        if (payload.Charge == null) return;

        string correlationId = payload.Charge.CorrelationId;

        // Woovi dispara TRANSACTION_RECEIVED por padrão pra cada PIX que cai na conta
        // (é o evento default — vem com charge.correlationID quando o PIX casa com uma cobrança).
        // CHARGE_COMPLETED é um evento derivado, só disparado se o webhook estiver inscrito
        // explicitamente nele. _NOT_SAME_CUSTOMER_PAYER é dinheiro recebido também (apenas
        // sinaliza que o pagador divergiu do cliente da cobrança — pra nós ainda é CAPTURED).
        TransactionStatus? newStatus = payload.Event switch
        {
            "OPENPIX:CHARGE_COMPLETED" => TransactionStatus.CAPTURED,
            "OPENPIX:CHARGE_COMPLETED_NOT_SAME_CUSTOMER_PAYER" => TransactionStatus.CAPTURED,
            "OPENPIX:TRANSACTION_RECEIVED" => TransactionStatus.CAPTURED,
            "OPENPIX:CHARGE_EXPIRED" => TransactionStatus.DECLINED,
            "OPENPIX:TRANSACTION_REFUND_RECEIVED" => TransactionStatus.REFUNDED,
            _ => null
        };

        if (newStatus == null) return;

        var transaction = await transactionRepository.GetByProviderTxIdAsync(correlationId);

        if (transaction == null)
        {
            logger.LogWarning("Transacao nao encontrada para CorrelationId OpenPix: {Id}", correlationId);
            return;
        }

        if (transaction.Provider != PaymentProvider.OPENPIX)
        {
            logger.LogWarning("Webhook OpenPix recebido para transacao {Id} que pertence ao provider {Provider}", transaction.Id, transaction.Provider);
            return;
        }

        // Per-seller AppId validation: verify authToken matches the seller's decrypted access token or platform AppId
        {
            var platformAppId = configuration["OpenPix:AppId"];
            bool tokenValidated = false;

            if (transaction.SellerId.HasValue)
            {
                var seller = await sellerRepository.GetByIdAsync(transaction.TenantId, transaction.SellerId.Value);
                if (seller?.EncryptedAccessToken != null)
                {
                    var sellerAppId = await securityService.DecryptAsync(seller.EncryptedAccessToken);
                    tokenValidated = SecureStringEquals(authToken, sellerAppId);
                }
            }

            // Fallback: check platform-level AppId
            if (!tokenValidated && !string.IsNullOrEmpty(platformAppId))
                tokenValidated = SecureStringEquals(authToken, platformAppId);

            if (!tokenValidated)
            {
                logger.LogWarning("Webhook OpenPix rejeitado: token nao corresponde a seller nem platform. Transacao: {Id}", transaction.Id);
                return;
            }
        }

        if (transaction.Status == newStatus.Value) return;

        if (!Transaction.IsValidTransition(transaction.Status, newStatus.Value))
        {
            logger.LogWarning("OpenPix webhook ignorado: transicao invalida {From} -> {To} para TX {TxId}",
                transaction.Status, newStatus.Value, transaction.Id);
            return;
        }

        await unitOfWork.BeginAsync();
        try
        {
            // Capture old status before SetStatusAsync (ExecuteUpdateAsync doesn't mutate in-memory entity)
            var oldStatus = transaction.Status;

            // Use ExecuteUpdateAsync to bypass xmin concurrency token — avoids being
            // affected if DetachAllEntities() is called during ledger retry below
            await transactionRepository.SetStatusAsync(transaction.Id, newStatus.Value);

            // Refresh tracked entity so SplitProcessor (and any other downstream reader
            // in the same DbContext) sees CAPTURED instead of the stale PROCESSING.
            await transactionRepository.ReloadAsync(transaction);

            if (newStatus == TransactionStatus.CAPTURED && transaction.SellerId.HasValue && transaction.NetAmount.HasValue)
            {
                // PaymentIntent-based collision guard
                if (transaction.PaymentIntentId.HasValue)
                {
                    bool won = await paymentIntentRepository.TryCaptureAsync(transaction.PaymentIntentId.Value, transaction.Id);
                    if (!won)
                    {
                        logger.LogCritical(
                            "[COLLISION] PaymentIntent {IntentId} already captured. " +
                            "Late TX {LateTxId} ({LateMethod}) will NOT credit ledger. Auto-refund required.",
                            transaction.PaymentIntentId.Value, transaction.Id, transaction.PaymentType);

                        await unitOfWork.CommitAsync();

                        // Enqueue reconciliation so DOUBLE_CAPTURE issue is created and auto-refund is tracked
                        backgroundJobs.Enqueue<IReconciliationService>(
                            svc => svc.ReconcileTransactionAsync(transaction.TenantId, transaction.Id, CancellationToken.None));

                        return;
                    }
                }
                else if (!string.IsNullOrEmpty(transaction.ExternalReferenceId))
                {
                    // Fallback for transactions created before PaymentIntent migration
                    var existing = await transactionRepository.GetCapturedByExternalReferenceAsync(
                        transaction.TenantId, transaction.ExternalReferenceId, transaction.Id);

                    if (existing != null)
                    {
                        logger.LogCritical(
                            "[COLLISION] Order {OrderId} already paid by TX {ExistingTxId} ({ExistingMethod}). " +
                            "Late payment TX {LateTxId} ({LateMethod}) will NOT credit ledger. Auto-refund required.",
                            transaction.ExternalReferenceId, existing.Id, existing.PaymentType,
                            transaction.Id, transaction.PaymentType);

                        await unitOfWork.CommitAsync();

                        // Enqueue reconciliation so DOUBLE_CAPTURE issue is created and auto-refund is tracked
                        backgroundJobs.Enqueue<IReconciliationService>(
                            svc => svc.ReconcileTransactionAsync(transaction.TenantId, transaction.Id, CancellationToken.None));

                        return;
                    }
                }

                var rail = railRouter.ResolveRailForTransaction(transaction);

                bool hasSplits = await transactionRepository.HasSplitsAsync(transaction.Id);
                if (hasSplits)
                {
                    // Split flow: PLATFORM_RECEIVABLE → SPLIT_CLEARING (held until SplitProcessor distributes)
                    await ledgerService.CreditSplitClearingAsync(
                        transaction.TenantId,
                        transaction.NetAmount.Value,
                        $"Liquidacao OpenPix [Split] Ref: {transaction.Id}",
                        transaction.Id.ToString());
                }
                else
                {
                    // Standard flow: PLATFORM_RECEIVABLE → seller WALLET/FUTURE_RECEIVABLES
                    await ledgerService.RecordIncomingFundsAsync(
                        transaction.TenantId,
                        transaction.SellerId.Value,
                        transaction.NetAmount.Value,
                        rail.CaptureAccountType,
                        $"Liquidacao OpenPix Ref: {transaction.Id}");
                }

                // Record platform margin breakdown (fee → provider cost + margin)
                if (transaction.PlatformFeeAmount.HasValue && transaction.PlatformFeeAmount.Value > 0)
                {
                    await ledgerService.RecordPlatformMarginAsync(
                        transaction.TenantId,
                        transaction.PlatformFeeAmount.Value,
                        transaction.ProviderCostAmount ?? 0m,
                        $"Margem TX {transaction.Id}",
                        transaction.Id.ToString());
                }
            }

            // OpenPix refund: política gross integral (mesmo padrão Stripe/portal/retry).
            // Seller absorve o gross integral, margem fica com a plataforma pra
            // cobrir o custo do provider (não recuperável).
            if (newStatus == TransactionStatus.REFUNDED && transaction.SellerId.HasValue)
            {
                var breakdown = Application.Modules.Transactions.Services.RefundCalculator
                    .Calculate(transaction, transaction.Amount);

                var activeSplitTransfers = await splitTransferRepository.GetByTransactionIdAsync(transaction.TenantId, transaction.Id);
                decimal totalSplitRemaining = activeSplitTransfers
                    .Where(t => t.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PAID or SplitTransferStatus.PARTIALLY_REVERSED)
                    .Sum(t => t.RemainingAmount);

                if (totalSplitRemaining == 0 && breakdown.SellerTotalDebit > 0)
                {
                    // No splits — debit primary seller direto pelo gross.
                    await ledgerService.DebitSellerAsync(
                        transaction.TenantId,
                        transaction.SellerId.Value,
                        breakdown.SellerTotalDebit,
                        $"Refund OpenPix — TX {transaction.Id} (gross)",
                        transaction.Id.ToString());
                }

                // NÃO revertemos margem (política gross — margem fica com a plataforma).

                // Reverse splits (includes primary residual transfer)
                await ReverseSplitsProportionallyAsync(transaction, transaction.Amount);
            }

            await unitOfWork.CommitAsync();

            // M5: Dispatch domain event manually since SetStatusAsync bypasses ChangeTracker
            try
            {
                await domainEventDispatcher.DispatchAsync([new TransactionStatusChangedEvent(
                    transaction.Id, transaction.TenantId, oldStatus, newStatus.Value,
                    transaction.SellerId, transaction.NetAmount, transaction.PaymentType, transaction.ProviderTxId)]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao despachar TransactionStatusChangedEvent para TX {TxId} (OpenPix). Evento nao-critico.", transaction.Id);
            }

            logger.LogInformation("Transacao {Id} atualizada para {Status} via OpenPix", transaction.Id, newStatus);
        }
        catch (Exception ex)
        {
            await unitOfWork.RollbackAsync();
            logger.LogError(ex, "Erro ao processar Webhook OpenPix para {CorrelationId}", correlationId);
            throw;
        }
    }

    private async Task HandleAccountRegisterEventAsync(OpenPixWebhookDto payload, string? authToken = null)
    {
        // Defense in depth: reject if auth token is absent or invalid.
        // The API filter already blocks unauthenticated requests, but the service layer
        // must not trust that the filter ran (e.g., internal calls, future refactoring).
        if (string.IsNullOrEmpty(authToken))
        {
            logger.LogWarning("Webhook OpenPix account-register rejeitado: token ausente (defense in depth)");
            return;
        }

        var platformAppId = configuration["OpenPix:AppId"];
        if (!SecureStringEquals(authToken, platformAppId))
        {
            logger.LogWarning("Webhook OpenPix account-register com token invalido");
            return;
        }

        string? correlationId = payload.AccountRegister?.CorrelationId;
        if (string.IsNullOrEmpty(correlationId)) return;

        var seller = await sellerRepository.GetByExternalAccountIdAsync(correlationId);
        if (seller == null)
        {
            logger.LogWarning("Seller nao encontrado para account-register CorrelationId: {Id}", correlationId);
            return;
        }

        switch (payload.Event)
        {
            case "ACCOUNT_REGISTER_APPROVED":
                seller.Activate();
                logger.LogInformation("Seller {Id} aprovado na OpenPix", seller.Id);
                break;

            case "ACCOUNT_REGISTER_REJECTED":
                seller.Suspend();
                logger.LogWarning("Seller {Id} rejeitado na OpenPix", seller.Id);
                break;

            case "ACCOUNT_REGISTER_PENDING":
                logger.LogInformation("Seller {Id} pendente de aprovacao na OpenPix", seller.Id);
                return;
        }

        await sellerRepository.SaveChangesAsync();
    }

    public async Task<WebhookEndpointResponseDto> CreateEndpointAsync(Guid tenantId, CreateWebhookEndpointDto request, Guid? sellerId = null)
    {
        // DNS-level SSRF check (moved from auto-validator to service layer because MustAsync
        // is incompatible with FluentValidation auto-validation's synchronous pipeline).
        if (await Validators.CreateWebhookEndpointDtoValidator.ResolvesToPrivateIpAsync(request.Url))
            throw new ValidationException("Webhook.SsrfBlocked", "A URL do webhook resolve para um endereço IP privado ou reservado.");

        // Bloqueia URL duplicada habilitada no mesmo escopo (tenant-wide vs producer-scoped).
        // Endpoints desabilitados não conflitam — o seller pode reativar/recadastrar depois de Disable.
        // (Race entre 2 cadastros simultâneos é coberto pelo unique partial index na DB.)
        var existing = await webhookEndpointRepository.GetEnabledByUrlAsync(tenantId, request.Url, sellerId);
        if (existing != null)
            throw new ConflictException(
                "Webhook.UrlAlreadyRegistered",
                $"Já existe um webhook ativo com esta URL (id {existing.Id}). Edite o existente ou desabilite-o antes de cadastrar de novo.");

        // Verificação de aceite: a URL precisa responder 200 a um payload sintético
        // antes de ser cadastrada. Se o endpoint não estiver pronto pra receber, o seller
        // recebe erro descritivo e nada é persistido.
        var probe = await webhookProbeClient.ProbeAsync(request.Url, request.Secret, "webhook.test");
        if (!probe.Success)
        {
            string detail = probe.Error
                ?? (probe.StatusCode > 0 ? $"O endpoint respondeu HTTP {probe.StatusCode}." : "O endpoint não respondeu.");
            throw new ValidationException(
                "Webhook.VerificationFailed",
                $"Não foi possível verificar a URL do webhook: {detail} Tente novamente quando o endpoint estiver respondendo HTTP 200.");
        }

        var encryptedSecret = await securityService.EncryptAsync(request.Secret);
        var result = WebhookEndpoint.Create(tenantId, request.Url, encryptedSecret, request.Events, sellerId);

        if (result.IsFailure)
            throw new ValidationException(result.Error.Code, result.Error.Description);

        var endpoint = result.Value;
        webhookEndpointRepository.Add(endpoint);
        await webhookEndpointRepository.SaveChangesAsync();

        return new WebhookEndpointResponseDto(endpoint.Id, endpoint.Url, endpoint.Events, endpoint.Enabled, endpoint.CreatedAt, endpoint.SellerId);
    }

    public async Task<WebhookTestResultDto> TestEndpointAsync(Guid tenantId, Guid endpointId, string eventType)
    {
        var endpoint = await webhookEndpointRepository.GetByIdAsync(tenantId, endpointId)
            ?? throw new NotFoundException("WebhookEndpoint.NotFound", $"Webhook endpoint {endpointId} nao encontrado.");

        string secret = await securityService.DecryptAsync(endpoint.Secret);
        return await webhookProbeClient.ProbeAsync(endpoint.Url, secret, eventType);
    }

    public Task<WebhookTestResultDto> ProbeEndpointAsync(string url, string secret, string eventType, CancellationToken ct = default)
        => webhookProbeClient.ProbeAsync(url, secret, eventType, ct);

    public async Task<RotateWebhookSecretResultDto> RotateSecretAsync(Guid tenantId, Guid endpointId)
    {
        var endpoint = await webhookEndpointRepository.GetByIdAsync(tenantId, endpointId)
            ?? throw new NotFoundException("WebhookEndpoint.NotFound", $"Webhook endpoint {endpointId} nao encontrado.");

        // Novo secret: 32 bytes hex (mesmo formato do "Gerar" do form de criação).
        string newSecret = CryptoUtils.GenerateRandomHex(32);

        // Probe ANTES de persistir: garante que o seller já configurou o servidor dele
        // pra aceitar o novo secret. Cutover atômico — sem janela de graça (decisão de
        // produto). Se o probe falhar, nada muda.
        var probe = await webhookProbeClient.ProbeAsync(endpoint.Url, newSecret, "webhook.test");
        if (!probe.Success)
        {
            string detail = probe.Error
                ?? (probe.StatusCode > 0 ? $"O endpoint respondeu HTTP {probe.StatusCode}." : "O endpoint não respondeu.");
            throw new ValidationException(
                "Webhook.RotationFailed",
                $"Não foi possível rotacionar o segredo: {detail} Atualize o segredo no seu servidor primeiro e tente novamente.");
        }

        string encrypted = await securityService.EncryptAsync(newSecret);
        endpoint.UpdateSecret(encrypted);
        webhookEndpointRepository.Update(endpoint);
        await webhookEndpointRepository.SaveChangesAsync();

        logger.LogInformation("Webhook secret rotacionado | Tenant: {TenantId} | Endpoint: {EndpointId}", tenantId, endpointId);
        return new RotateWebhookSecretResultDto(newSecret);
    }

    public async Task<IEnumerable<WebhookEndpointResponseDto>> ListEndpointsAsync(Guid tenantId)
    {
        var endpoints = await webhookEndpointRepository.GetActiveByTenantAsync(tenantId);
        return endpoints.Select(e => new WebhookEndpointResponseDto(e.Id, e.Url, e.Events, e.Enabled, e.CreatedAt, e.SellerId));
    }

    public async Task<PagedResult<WebhookEndpointResponseDto>> ListEndpointsPagedAsync(Guid tenantId, int page, int pageSize)
    {
        var (skip, take, normalizedPage) = PagedResult<WebhookEndpointResponseDto>.Normalize(page, pageSize);
        var (items, totalCount) = await webhookEndpointRepository.GetPagedAsync(tenantId, skip, take);

        return new PagedResult<WebhookEndpointResponseDto>(
            Items: items.Select(e => new WebhookEndpointResponseDto(e.Id, e.Url, e.Events, e.Enabled, e.CreatedAt, e.SellerId)).ToList(),
            TotalCount: totalCount,
            Page: normalizedPage,
            PageSize: take);
    }

    public async Task<PagedResult<WebhookEndpointResponseDto>> ListEndpointsByScopePagedAsync(Guid tenantId, Guid? sellerId, int page, int pageSize)
    {
        var (skip, take, normalizedPage) = PagedResult<WebhookEndpointResponseDto>.Normalize(page, pageSize);
        var (items, totalCount) = await webhookEndpointRepository.GetPagedByScopeAsync(tenantId, sellerId, skip, take);

        return new PagedResult<WebhookEndpointResponseDto>(
            Items: items.Select(e => new WebhookEndpointResponseDto(e.Id, e.Url, e.Events, e.Enabled, e.CreatedAt, e.SellerId)).ToList(),
            TotalCount: totalCount,
            Page: normalizedPage,
            PageSize: take);
    }

    public async Task DeleteEndpointAsync(Guid tenantId, Guid endpointId)
    {
        var endpoint = await webhookEndpointRepository.GetByIdAsync(tenantId, endpointId)
            ?? throw new NotFoundException("WebhookEndpoint.NotFound", $"Webhook endpoint {endpointId} nao encontrado.");

        endpoint.Disable();
        webhookEndpointRepository.Update(endpoint);
        await webhookEndpointRepository.SaveChangesAsync();
    }

    public async Task<PagedResult<WebhookDeliveryResponseDto>> GetDeliveriesAsync(Guid tenantId, Guid endpointId, int page, int pageSize)
    {
        _ = await webhookEndpointRepository.GetByIdAsync(tenantId, endpointId)
            ?? throw new NotFoundException("WebhookEndpoint.NotFound", $"Webhook endpoint {endpointId} nao encontrado.");

        var (skip, take, normalizedPage) = PagedResult<WebhookDeliveryResponseDto>.Normalize(page, pageSize);
        var (items, totalCount) = await webhookDeliveryRepository.GetByEndpointPagedAsync(endpointId, skip, take);

        return new PagedResult<WebhookDeliveryResponseDto>(
            Items: items.Select(d => new WebhookDeliveryResponseDto(
                d.Id, d.EventId, d.EventType, d.ResponseCode, d.Success,
                d.Duration, d.Status, d.RetryCount, d.LastError, d.CreatedAt)).ToList(),
            TotalCount: totalCount,
            Page: normalizedPage,
            PageSize: take);
    }

    public async Task RetryDeliveryAsync(Guid tenantId, Guid endpointId, Guid deliveryId)
    {
        _ = await webhookEndpointRepository.GetByIdAsync(tenantId, endpointId)
            ?? throw new NotFoundException("WebhookEndpoint.NotFound", $"Webhook endpoint {endpointId} nao encontrado.");

        var delivery = await webhookDeliveryRepository.GetByIdAsync(tenantId, deliveryId)
            ?? throw new NotFoundException("WebhookDelivery.NotFound", $"Webhook delivery {deliveryId} nao encontrada.");

        if (delivery.EndpointId != endpointId)
            throw new NotFoundException("WebhookDelivery.NotFound", $"Webhook delivery {deliveryId} nao encontrada.");

        if (delivery.Success)
            throw new BusinessException("WebhookDelivery.AlreadySucceeded", "Delivery ja foi entregue com sucesso.");

        delivery.ResetForManualRetry();
        webhookDeliveryRepository.Update(delivery);
        await webhookDeliveryRepository.SaveChangesAsync();

        logger.LogInformation("Manual retry agendado para delivery {DeliveryId} do endpoint {EndpointId}", deliveryId, endpointId);
    }

    public async Task<DeadLetterSummaryDto> GetDeadLettersAsync(Guid tenantId, int limit = 50)
    {
        var count = await webhookDeliveryRepository.GetDeadLetterCountAsync(tenantId);
        var items = await webhookDeliveryRepository.GetDeadLettersAsync(tenantId, limit);

        return new DeadLetterSummaryDto(
            TotalCount: count,
            Items: items.Select(d => new WebhookDeliveryResponseDto(
                d.Id, d.EventId, d.EventType, d.ResponseCode,
                d.Success, d.Duration, d.Status, d.RetryCount,
                d.LastError, d.CreatedAt)).ToList()
        );
    }

    public async Task<int> RetryAllDeadLettersAsync(Guid tenantId)
    {
        var deadLetters = await webhookDeliveryRepository.GetDeadLettersAsync(tenantId, 500);
        if (deadLetters.Count == 0) return 0;

        foreach (var delivery in deadLetters)
        {
            delivery.ResetForManualRetry();
        }

        await webhookDeliveryRepository.SaveChangesAsync();

        logger.LogInformation("Bulk retry agendado para {Count} dead letter(s)", deadLetters.Count);
        return deadLetters.Count;
    }

    /// <summary>
    /// Proportionally reverses splits for a refund/chargeback.
    /// Each recipient is debited their proportional share of the net refund amount.
    /// Combined with the primary seller's reduced debit, the total equals the full net refund.
    /// </summary>
    private async Task ReverseSplitsProportionallyAsync(Transaction transaction, decimal refundDelta)
    {
        if (transaction.Amount <= 0) return;

        var transfers = await splitTransferRepository.GetByTransactionIdAsync(transaction.TenantId, transaction.Id);
        var reversibleTransfers = transfers
            .Where(t => t.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PAID or SplitTransferStatus.PARTIALLY_REVERSED)
            .Where(t => t.RemainingAmount > 0)
            .ToList();

        if (reversibleTransfers.Count == 0) return;

        decimal totalReversibleAmount = reversibleTransfers.Sum(t => t.RemainingAmount);
        if (totalReversibleAmount <= 0) return;

        decimal netAmount = transaction.NetAmount ?? transaction.Amount;
        // Full refund → reverse all splits entirely
        bool isFullRefund = refundDelta >= transaction.Amount;

        // For partial refund: each recipient's reversal is proportional to the net refund
        // proportionalRefund = refundDelta * (netAmount / amount)
        // recipientReversal = proportionalRefund * (recipientRemainingAmount / totalReversibleAmount)
        decimal proportionalRefund = isFullRefund ? totalReversibleAmount : RoundingPolicy.Round(refundDelta * (netAmount / transaction.Amount));
        // Cap at what's actually available
        proportionalRefund = Math.Min(proportionalRefund, totalReversibleAmount);

        decimal totalReversed = 0m;

        foreach (var transfer in reversibleTransfers)
        {
            decimal reversalAmount;
            if (isFullRefund)
            {
                reversalAmount = transfer.RemainingAmount;
            }
            else
            {
                // Each recipient's reversal proportional to their remaining balance
                reversalAmount = RoundingPolicy.Proportional(proportionalRefund, transfer.RemainingAmount, totalReversibleAmount);
            }

            if (reversalAmount <= 0) continue;

            // Pre-validate state transition before moving ledger money
            var targetStatus = (isFullRefund || reversalAmount >= transfer.RemainingAmount)
                ? SplitTransferStatus.REVERSED
                : SplitTransferStatus.PARTIALLY_REVERSED;

            if (!SplitTransfer.IsValidTransition(transfer.Status, targetStatus))
            {
                logger.LogCritical(
                    "[SPLIT] Cannot reverse SplitTransfer {TransferId}: invalid transition {From}→{To}. Skipping ledger move. TX: {TxId}",
                    transfer.Id, transfer.Status, targetStatus, transaction.Id);
                continue;
            }

            try
            {
                // Return funds: debit recipient's WALLET → credit SPLIT_CLEARING
                await ledgerService.ReturnToClearingAsync(
                    transaction.TenantId,
                    transfer.RecipientSellerId,
                    reversalAmount,
                    $"Split reversal — TX {transaction.Id} (refund delta: R${refundDelta})",
                    transaction.Id.ToString());

                Result reverseResult;
                if (isFullRefund || reversalAmount >= transfer.RemainingAmount)
                    reverseResult = transfer.Reverse();
                else
                    reverseResult = transfer.PartialReverse(reversalAmount);

                if (reverseResult.IsFailure)
                {
                    // Should not happen since we pre-validated, but defensive
                    logger.LogCritical(
                        "[LEDGER] CRITICAL: Ledger reversed R${Amount} for SplitTransfer {TransferId} (seller {SellerId}) but status transition failed: {Error}. Manual reconciliation required.",
                        reversalAmount, transfer.Id, transfer.RecipientSellerId, reverseResult.Error.Description);
                }

                splitTransferRepository.Update(transfer);
                totalReversed += reversalAmount;

                logger.LogInformation(
                    "Split reversal: R${Amount} devolvido ao SPLIT_CLEARING de seller {SellerId} (SplitTransfer {TransferId})",
                    reversalAmount, transfer.RecipientSellerId, transfer.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falha ao reverter split {TransferId} para seller {SellerId}. Amount: R${Amount}",
                    transfer.Id, transfer.RecipientSellerId, reversalAmount);
            }
        }

        // Drain SPLIT_CLEARING → PLATFORM_PAYOUT (money leaves system via refund)
        if (totalReversed > 0)
        {
            await ledgerService.DrainClearingForRefundAsync(
                transaction.TenantId,
                totalReversed,
                $"Split clearing drain — TX {transaction.Id} refund R${refundDelta}",
                transaction.Id.ToString());
        }

        await splitTransferRepository.SaveChangesAsync();
    }

    /// <summary>
    /// Timing-safe string comparison to prevent timing side-channel attacks on webhook tokens.
    /// </summary>
    private static bool SecureStringEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }

    /// <summary>
    /// Label PT-BR pro método de pagamento. Usado em mensagens user-facing
    /// (notificações in-app). Mantido aqui (em vez de em formatters/enums)
    /// porque o NotificationService precisa de string já localizada.
    /// </summary>
    private static string PaymentMethodLabel(PaymentType type) => type switch
    {
        PaymentType.CREDIT_CARD => "cartão de crédito",
        PaymentType.DEBIT_CARD => "cartão de débito",
        PaymentType.PIX => "PIX",
        PaymentType.BOLETO => "boleto",
        _ => "outro método"
    };
}
