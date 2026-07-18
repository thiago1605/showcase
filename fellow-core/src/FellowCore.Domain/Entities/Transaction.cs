using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Entities;

public class Transaction : AggregateRoot<Guid>
{
    [Required]
    public Guid TenantId { get; private set; }
    public virtual Tenant Tenant { get; private set; } = null!;

    public Guid? SellerId { get; private set; }
    public virtual Seller? Seller { get; private set; }
    public Guid? CustomerId { get; private set; }
    public virtual Customer? Customer { get; private set; }
    public Guid? PaymentMethodId { get; private set; }
    public virtual PaymentMethod? PaymentMethod { get; private set; }

    public Guid? PaymentIntentId { get; private set; }
    public virtual PaymentIntent? PaymentIntent { get; private set; }

    public decimal Amount { get; private set; }
    public decimal? FeeAmount { get; private set; }
    public decimal? NetAmount { get; private set; }

    /// <summary>The fee FellowCore charges the seller (platform fee).</summary>
    public decimal? PlatformFeeAmount { get; private set; }

    /// <summary>The estimated cost charged by the payment provider (from ProviderCostSchedule at capture time).</summary>
    public decimal? ProviderCostAmount { get; private set; }

    /// <summary>The actual cost from the provider's settlement report (populated during reconciliation).</summary>
    public decimal? ProviderCostActualAmount { get; private set; }

    /// <summary>Platform margin: PlatformFeeAmount - ProviderCostAmount.</summary>
    public decimal? PlatformMarginAmount { get; private set; }

    public FeeAllocationPolicy FeeAllocationPolicy { get; private set; } = FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES;

    [MaxLength(3)]
    public string Currency { get; private set; } = "BRL";

    public DateTime? ExpectedSettlementDate { get; private set; }
    public SettlementStatus SettlementStatus { get; private set; } = SettlementStatus.PENDING;
    public TransactionStatus Status { get; private set; } = TransactionStatus.CREATED;

    /// <summary>
    /// Modelo de liquidação registrado na captura. INSTALLMENT (default) = parcela a
    /// parcela; ADVANCE = antecipação automática (1 parcela em D+30). Imutável após
    /// captura — auditoria contábil.
    /// </summary>
    public SettlementMode SettlementMode { get; private set; } = SettlementMode.INSTALLMENT;

    /// <summary>
    /// Valor cobrado da antecipação (= NetAmount × plan.AdvancePercentFee/100).
    /// Não-nulo só quando <see cref="SettlementMode"/> = ADVANCE. Vai pra
    /// PLATFORM_MARGIN no ledger no momento da captura.
    /// </summary>
    public decimal? AdvanceFeeAmount { get; private set; }

    /// <summary>
    /// Override per-TX da antecipação automática. Setado no <c>CreateTransactionDto</c>:
    ///   - null (default): segue o flag do seller (<see cref="Seller.AutoAdvanceSettlement"/>)
    ///   - true: força ADVANCE mesmo se seller não tem auto-advance ligado
    ///   - false: força INSTALLMENT mesmo se seller tem auto-advance ligado
    /// Persistido no momento da criação da TX; capture hook usa pra decidir o modo.
    /// </summary>
    public bool? AdvanceOptIn { get; private set; }

    /// <summary>
    /// Quantas parcelas a Stripe já liberou pra plataforma no Connect balance
    /// (proxied pelo passar do tempo no MVP). Drives <c>AdvanceSettlementReconciler</c>
    /// pra devolver reserve gradualmente conforme o caixa real entra.
    /// Idempotência: reconciler só processa quando &lt; Installments.
    /// </summary>
    public int AdvanceRecoveredInstallmentCount { get; private set; }

    /// <summary>
    /// Stripe charge ID (ch_...) extraído do payload `payment_intent.succeeded`.
    /// Drives o <c>StripeAdvanceReconciler</c> pra mapear <c>balance_transaction.source</c>
    /// de volta pra esta TX. Null pra TXs OpenPix/legacy.
    /// </summary>
    public string? StripeChargeId { get; private set; }

    /// <summary>Setado pelo webhook handler ao confirmar a captura.</summary>
    public void SetStripeChargeId(string chargeId)
    {
        if (string.IsNullOrWhiteSpace(chargeId)) return;
        StripeChargeId = chargeId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Incrementa contador de parcelas recuperadas (chamado pelo reconciler).</summary>
    public void IncrementRecoveredInstallments(int by = 1)
    {
        if (by <= 0) return;
        AdvanceRecoveredInstallmentCount = Math.Min(Installments, AdvanceRecoveredInstallmentCount + by);
        UpdatedAt = DateTime.UtcNow;
    }

    public PaymentProvider Provider { get; private set; }
    public string? ProviderTxId { get; private set; }
    public JsonDocument? ProviderMetadata { get; private set; }
    public string? ProviderInvoiceUrl { get; private set; }

    public string? Nsu { get; private set; }
    public string? AuthorizationCode { get; private set; }

    public int Installments { get; private set; } = 1;
    public PaymentType PaymentType { get; private set; }
    public string? WalletType { get; private set; }

    /// <summary>Merchant's order ID — used for cross-method idempotency (only one payment per order can succeed).</summary>
    [MaxLength(200)]
    public string? ExternalReferenceId { get; private set; }

    public string? IdempotencyKey { get; private set; }
    public string? IpAddress { get; private set; }
    public double? RiskScore { get; private set; }

    public JsonDocument? Metadata { get; private set; }
    public decimal RefundedAmount { get; private set; }

    [MaxLength(256)]
    public string? PayerEmail { get; private set; }

    [MaxLength(200)]
    public string? PayerName { get; private set; }

    /// <summary>
    /// CPF/CNPJ do pagador (digits only, 11 ou 14). Captured at payment time from the
    /// public checkout for card/Pix/Boleto flows. Used for fiscal/receipt and antifraude.
    /// Wallets (Apple/Google Pay/Link) may not provide it — stays null in that case until
    /// webhook enrichment lands.
    /// </summary>
    [MaxLength(14)]
    public string? PayerDocument { get; private set; }

    public string? ErrorMessage { get; private set; }
    public string? Description { get; private set; }

    public int DunningAttempts { get; private set; }
    public DateTime? NextDunningAt { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    [Timestamp]
    public uint RowVersion { get; private set; }

    public virtual ICollection<TransactionEvent> Timeline { get; private set; } = new List<TransactionEvent>();
    public virtual ICollection<TransactionSplit> Splits { get; private set; } = new List<TransactionSplit>();

    protected Transaction() { }

    public static Result<Transaction> Create(
        Guid tenantId,
        decimal amount,
        PaymentType paymentType,
        PaymentProvider provider,
        int installments,
        decimal? feeAmount,
        decimal? netAmount,
        DateTime? expectedSettlementDate,
        string? providerTxId,
        Guid? sellerId = null,
        Guid? customerId = null,
        string? description = null,
        string? idempotencyKey = null,
        string? ipAddress = null,
        string? externalReferenceId = null,
        FeeAllocationPolicy? feeAllocationPolicy = null,
        bool? advanceOptIn = null,
        JsonDocument? metadata = null,
        DateTime? now = null)
    {
        if (amount <= 0)
            return Result.Failure<Transaction>(Error.Validation("Transaction.InvalidAmount", "O valor da transação deve ser maior que zero."));

        var timestamp = now ?? DateTime.UtcNow;

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SellerId = sellerId,
            CustomerId = customerId,
            Amount = amount,
            FeeAmount = feeAmount,
            NetAmount = netAmount,
            Currency = "BRL",
            Status = TransactionStatus.PROCESSING,
            PaymentType = paymentType,
            Installments = installments > 0 ? installments : 1,
            Provider = provider,
            ProviderTxId = providerTxId,
            ExpectedSettlementDate = expectedSettlementDate,
            FeeAllocationPolicy = feeAllocationPolicy ?? FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES,
            Description = description,
            ExternalReferenceId = externalReferenceId,
            IdempotencyKey = idempotencyKey,
            IpAddress = ipAddress,
            AdvanceOptIn = advanceOptIn,
            Metadata = metadata,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };

        transaction.AddDomainEvent(new TransactionCreatedEvent(transaction.Id, tenantId, amount, paymentType, provider));

        return Result.Success(transaction);
    }

    private static readonly Dictionary<TransactionStatus, HashSet<TransactionStatus>> AllowedTransitions = new()
    {
        [TransactionStatus.CREATED] = [TransactionStatus.PROCESSING, TransactionStatus.VOIDED, TransactionStatus.FAILED],
        [TransactionStatus.PROCESSING] = [TransactionStatus.CAPTURED, TransactionStatus.AUTHORIZED, TransactionStatus.DECLINED, TransactionStatus.VOIDED, TransactionStatus.FAILED],
        [TransactionStatus.AUTHORIZED] = [TransactionStatus.CAPTURED, TransactionStatus.VOIDED, TransactionStatus.FAILED],
        [TransactionStatus.CAPTURED] = [TransactionStatus.REFUNDED, TransactionStatus.CHARGEBACKERROR],
        [TransactionStatus.DECLINED] = [TransactionStatus.PROCESSING, TransactionStatus.FAILED],
        [TransactionStatus.CHARGEBACKERROR] = [TransactionStatus.CAPTURED],
    };

    public static bool IsValidTransition(TransactionStatus from, TransactionStatus to)
        => AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public Result UpdateStatus(TransactionStatus newStatus, DateTime? now = null)
    {
        if (Status == newStatus)
            return Result.Success();

        if (!IsValidTransition(Status, newStatus))
            return Result.Failure(Error.Business("Transaction.InvalidTransition",
                $"Transicao de {Status} para {newStatus} nao e permitida."));

        var oldStatus = Status;
        Status = newStatus;
        UpdatedAt = now ?? DateTime.UtcNow;

        AddTimelineEvent(newStatus);
        AddDomainEvent(new TransactionStatusChangedEvent(Id, TenantId, oldStatus, newStatus, SellerId, NetAmount, PaymentType, ProviderTxId));

        if (newStatus == TransactionStatus.DECLINED && DunningAttempts == 0)
            ScheduleDunning();

        return Result.Success();
    }

    /// <summary>
    /// Valida se um refund é permitido sem mutar o estado. Usado pra checar
    /// pré-condições antes de chamar o provider de pagamentos — assim, se o
    /// provider falhar, não deixamos `RefundedAmount` corrompido localmente.
    /// </summary>
    public Result CanBeRefunded(decimal amount)
    {
        if (Status != TransactionStatus.CAPTURED)
            return Result.Failure(Error.Business("Transaction.NotRefundable", $"Transacao com status {Status} nao pode ser reembolsada."));

        if (amount <= 0)
            return Result.Failure(Error.Validation("Refund.InvalidAmount", "Valor do reembolso deve ser maior que zero."));

        if (RefundedAmount + amount > Amount)
            return Result.Failure(Error.Validation("Refund.ExceedsAmount", "Valor total reembolsado excede o valor da transacao."));

        return Result.Success();
    }

    public Result Refund(decimal amount, DateTime? now = null)
    {
        var validation = CanBeRefunded(amount);
        if (validation.IsFailure)
            return validation;

        RefundedAmount += amount;
        UpdatedAt = now ?? DateTime.UtcNow;

        if (RefundedAmount >= Amount)
            UpdateStatus(TransactionStatus.REFUNDED);

        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status is not (TransactionStatus.CREATED or TransactionStatus.PROCESSING))
            return Result.Failure(Error.Business("Transaction.NotCancellable", $"Transacao com status {Status} nao pode ser cancelada."));

        return UpdateStatus(TransactionStatus.VOIDED);
    }

    public void MarkAsSettled()
    {
        SettlementStatus = SettlementStatus.SETTLED;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetWalletType(string? walletType)
    {
        WalletType = walletType;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddTimelineEvent(TransactionStatus status, JsonDocument? metadata = null)
    {
        var @event = TransactionEvent.Create(Id, status, metadata);
        Timeline.Add(@event);
    }

    /// <summary>
    /// Marca a TX como antecipada (modo ADVANCE) e registra o fee cobrado.
    /// Chamado uma única vez durante a captura — depois disso a TX é imutável
    /// no que diz respeito ao modo de liquidação.
    ///
    /// Não chamar pra modo INSTALLMENT (default já é INSTALLMENT no construtor).
    /// </summary>
    public Result MarkAsAdvanceSettlement(decimal advanceFeeAmount, DateTime? now = null)
    {
        if (SettlementMode == SettlementMode.ADVANCE)
            return Result.Failure(Error.Business("Transaction.AlreadyAdvanced",
                "TX já registrada como ADVANCE — re-marcar é bug, não permitido."));

        if (advanceFeeAmount < 0)
            return Result.Failure(Error.Validation("Transaction.InvalidAdvanceFee",
                "AdvanceFeeAmount não pode ser negativo."));

        SettlementMode = SettlementMode.ADVANCE;
        AdvanceFeeAmount = advanceFeeAmount;
        UpdatedAt = now ?? DateTime.UtcNow;
        return Result.Success();
    }

    public void AddSplit(string recipientId, string recipientType, decimal amount, decimal? percentage = null)
    {
        var split = TransactionSplit.Create(Id, recipientId, recipientType, amount, percentage);
        Splits.Add(split);
    }

    public void SetProviderTxId(string providerTxId, DateTime? now = null)
    {
        ProviderTxId = providerTxId;
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    public void SetPaymentIntentId(Guid paymentIntentId, DateTime? now = null)
    {
        PaymentIntentId = paymentIntentId;
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    public void SetPayerInfo(string? email, string? name, string? document = null, DateTime? now = null)
    {
        PayerEmail = email;
        PayerName = name;
        // Strip mask before persisting (frontend may send "000.000.000-00" or "00.000.000/0000-00").
        PayerDocument = string.IsNullOrWhiteSpace(document) ? null : System.Text.RegularExpressions.Regex.Replace(document, @"\D", "");
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    public void SetProviderInvoiceUrl(string url, DateTime? now = null)
    {
        ProviderInvoiceUrl = url;
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    /// <summary>
    /// Sets the margin breakdown for this transaction.
    /// PlatformMarginAmount is computed as platformFee - providerCost.
    /// </summary>
    public void SetMarginBreakdown(decimal platformFee, decimal providerCost, DateTime? now = null)
    {
        PlatformFeeAmount = platformFee;
        ProviderCostAmount = providerCost;
        PlatformMarginAmount = platformFee - providerCost;
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    /// <summary>
    /// Sets the actual provider cost from settlement reconciliation.
    /// </summary>
    public void SetActualProviderCost(decimal actualCost, DateTime? now = null)
    {
        ProviderCostActualAmount = actualCost;
        UpdatedAt = now ?? DateTime.UtcNow;
    }

    private static readonly int[] DunningDelayDays = [1, 3, 7, 14];
    public const int MaxDunningAttempts = 4;

    public bool IsDunningEligible()
        => Status == TransactionStatus.DECLINED
           && DunningAttempts < MaxDunningAttempts
           && NextDunningAt.HasValue
           && NextDunningAt.Value <= DateTime.UtcNow;

    public void ScheduleDunning(DateTime? now = null)
    {
        if (Status != TransactionStatus.DECLINED || DunningAttempts >= MaxDunningAttempts) return;
        var delayDays = DunningAttempts < DunningDelayDays.Length
            ? DunningDelayDays[DunningAttempts]
            : DunningDelayDays[^1];
        NextDunningAt = (now ?? DateTime.UtcNow).AddDays(delayDays);
    }

    public void RecordDunningAttempt(bool success, DateTime? now = null)
    {
        DunningAttempts++;
        if (success)
        {
            NextDunningAt = null;
        }
        else if (DunningAttempts < MaxDunningAttempts)
        {
            ScheduleDunning(now);
        }
        else
        {
            NextDunningAt = null;
        }
        UpdatedAt = now ?? DateTime.UtcNow;
    }
}
