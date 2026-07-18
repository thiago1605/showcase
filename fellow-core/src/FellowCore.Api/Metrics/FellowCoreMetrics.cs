using System.Diagnostics.Metrics;

namespace FellowCore.Api.Metrics;

/// <summary>
/// Custom OpenTelemetry metrics for FellowCore business observability.
/// Instruments are created once (singleton-safe) and recorded from any service layer.
/// </summary>
public sealed class FellowCoreMetrics
{
    public const string MeterName = "FellowCore";

    // Transaction metrics
    private readonly Counter<long> _transactionsTotal;
    private readonly Counter<long> _refundsTotal;

    // Payout metrics
    private readonly Counter<long> _payoutsTotal;
    private readonly Counter<long> _payoutsFailedTotal;
    private readonly Counter<long> _payoutsRetryTotal;
    private readonly Counter<long> _refundsRetryTotal;
    private readonly Counter<long> _zombieCancellationsTotal;
    private readonly ObservableGauge<double> _payoutsPendingAgeHours;

    // Provider metrics
    private readonly Histogram<double> _providerRequestDuration;
    private readonly Counter<long> _providerRequestsTotal;
    private readonly Counter<long> _providerErrorsTotal;
    private readonly ObservableGauge<int> _circuitBreakerState;

    // Webhook metrics
    private readonly Counter<long> _webhookDeliveriesTotal;
    private readonly Counter<long> _webhookDeliveryFailuresTotal;
    private readonly Counter<long> _webhookDuplicateTotal;
    private readonly Counter<long> _webhookInvalidSignatureTotal;

    // Operational metrics
    private readonly Counter<long> _splitDistributeIdempotencyHitTotal;
    private readonly Counter<long> _providerCostMismatchTotal;
    private readonly Counter<long> _platformMarginNegativeTotal;

    // Outbox metrics
    private readonly ObservableGauge<long> _outboxDeadLetterCount;

    // Notification outbox metrics (Sprint 2 — outbox pattern pra notifs in-app)
    private readonly Counter<long> _notificationOutboxProcessedTotal;
    private readonly Counter<long> _notificationOutboxFailedTotal;
    private readonly Counter<long> _notificationOutboxDeadLetterTotal;
    private readonly ObservableGauge<long> _notificationOutboxPending;

    // Reconciliation / Ledger metrics
    private readonly ObservableGauge<long> _reconciliationIssuesTotal;
    private readonly ObservableGauge<double> _ledgerGlobalImbalanceCents;

    // Split/Clearing metrics
    private readonly Counter<long> _splitDistributionsTotal;
    private readonly ObservableGauge<double> _splitClearingBalanceCents;
    private readonly ObservableGauge<double> _platformMarginCents;

    // Hybrid Settlement (ADVANCE mode) metrics
    private readonly Counter<long> _advanceCapturedTotal;
    private readonly Counter<long> _advanceFeeCollectedCents;
    private readonly Counter<long> _advanceAdvancedAmountCents;
    private readonly Counter<long> _advanceReversedTotal;
    private readonly Counter<long> _advanceThrottledTotal;

    // Sprint 1 #3: tier-based discount applied per TX. Label = tier name.
    private readonly Counter<long> _tierDiscountAppliedTotal;
    private readonly ObservableGauge<long> _advanceReserveRemainingCents;
    private readonly ObservableGauge<long> _advanceReserveCapacityCents;
    private readonly ObservableGauge<long> _sellerExposureCurrentCents;
    private readonly ObservableGauge<long> _sellerExposureThresholdCents;

    // Auth metrics
    private readonly Counter<long> _passwordResetTotal;

    // Dispute metrics
    private readonly ObservableGauge<long> _disputesOpenCount;

    // Hangfire metrics
    private readonly ObservableGauge<double> _hangfireLastHeartbeatSeconds;
    private readonly ObservableGauge<long> _hangfireFailedCount;

    // Health check metrics
    private readonly ObservableGauge<int> _healthCheckStatus;

    // Backing values for observable gauges (updated by background jobs / services)
    private double _payoutsPendingAgeHoursValue;
    private readonly Dictionary<string, int> _circuitBreakerStates = new();
    private long _outboxDeadLetterCountValue;
    private long _notificationOutboxPendingValue;
    private readonly Dictionary<string, long> _reconciliationIssuesBySeverity = new();
    private double _ledgerGlobalImbalanceCentsValue;
    private long _disputesOpenCountValue;
    // ADVANCE — gauges atualizados pelo MetricsCollectorWorker via callbacks.
    // _advanceReserveCapacityCents é o pico histórico (top-up max) usado pra calcular
    // pct_used no alerta AdvanceReserveLow; recalcula no boot via SUM(tenant tops).
    private long _advanceReserveRemainingCentsValue;
    private long _advanceReserveCapacityCentsValue;
    // Por-seller exposure mantido como dict label-by-id pra Prometheus emitir series
    // separadas e o alerta AdvanceSellerExposureHigh filtrar por seller_id.
    private readonly Dictionary<string, long> _sellerExposureCents = new();
    // Threshold per-seller — quando null no DB, snapshot usa o default global.
    // Alerta Prometheus compara: exposure_current > exposure_threshold.
    private readonly Dictionary<string, long> _sellerExposureThresholdsCents = new();
    private double _splitClearingBalanceCentsValue;
    private double _platformMarginCentsValue;
    private double _hangfireLastHeartbeatSecondsValue;
    private long _hangfireFailedCountValue;
    private readonly Dictionary<string, int> _healthCheckStatuses = new();

    public FellowCoreMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _transactionsTotal = meter.CreateCounter<long>(
            "fellowcore_transactions_total",
            description: "Total number of transactions processed");

        _refundsTotal = meter.CreateCounter<long>(
            "fellowcore_refunds_total",
            description: "Total number of refunds processed");

        _payoutsTotal = meter.CreateCounter<long>(
            "fellowcore_payouts_total",
            description: "Total number of payouts processed");

        _payoutsFailedTotal = meter.CreateCounter<long>(
            "fellowcore_payouts_failed_total",
            description: "Total number of failed payouts");

        _payoutsRetryTotal = meter.CreateCounter<long>(
            "fellowcore_payouts_retry_total",
            description: "Total number of payout retries scheduled");

        _refundsRetryTotal = meter.CreateCounter<long>(
            "fellowcore_refunds_retry_total",
            description: "Total number of refund retries scheduled");

        _zombieCancellationsTotal = meter.CreateCounter<long>(
            "fellowcore_transactions_zombie_cancelled_total",
            description: "Total number of stale CREATED/PROCESSING transactions cancelled by cleanup job");

        _payoutsPendingAgeHours = meter.CreateObservableGauge(
            "fellowcore_payouts_pending_age_hours",
            () => _payoutsPendingAgeHoursValue,
            unit: "h",
            description: "Age in hours of the oldest pending payout");

        _providerRequestDuration = meter.CreateHistogram<double>(
            "fellowcore_provider_request_duration_seconds",
            unit: "s",
            description: "Duration of payment provider API requests in seconds");

        _providerRequestsTotal = meter.CreateCounter<long>(
            "fellowcore_provider_requests_total",
            description: "Total number of provider API requests");

        _providerErrorsTotal = meter.CreateCounter<long>(
            "fellowcore_provider_errors_total",
            description: "Total number of provider API errors");

        _circuitBreakerState = meter.CreateObservableGauge(
            "fellowcore_circuit_breaker_state",
            () => ObserveCircuitBreakerStates(),
            description: "Circuit breaker state: 0=closed, 1=open, 2=half-open");

        _webhookDeliveriesTotal = meter.CreateCounter<long>(
            "fellowcore_webhook_deliveries_total",
            description: "Total number of webhook deliveries attempted");

        _webhookDeliveryFailuresTotal = meter.CreateCounter<long>(
            "fellowcore_webhook_delivery_failures_total",
            description: "Total number of failed webhook deliveries");

        _webhookDuplicateTotal = meter.CreateCounter<long>(
            "fellowcore_webhook_duplicate_total",
            description: "Total number of duplicate webhooks detected");

        _webhookInvalidSignatureTotal = meter.CreateCounter<long>(
            "fellowcore_webhook_invalid_signature_total",
            description: "Total number of webhooks with invalid signature");

        _splitDistributeIdempotencyHitTotal = meter.CreateCounter<long>(
            "fellowcore_split_distribute_idempotency_hit_total",
            description: "Total number of idempotent split distribute skip events");

        _providerCostMismatchTotal = meter.CreateCounter<long>(
            "fellowcore_provider_cost_mismatch_total",
            description: "Total number of provider cost estimate vs actual mismatches");

        _platformMarginNegativeTotal = meter.CreateCounter<long>(
            "fellowcore_platform_margin_negative_total",
            description: "Total number of transactions resulting in negative platform margin");

        _outboxDeadLetterCount = meter.CreateObservableGauge(
            "fellowcore_outbox_dead_letter_count",
            () => _outboxDeadLetterCountValue,
            description: "Current count of dead letter messages in outbox");

        // Notification outbox — observabilidade do pipeline async de notificações
        // in-app. Pending = pendentes não processadas ainda (gauge atualizado pelo
        // próprio processor a cada execução). Processed/Failed/DLQ = counters
        // incrementados em cada batch — taxa de processamento + saúde do worker.
        _notificationOutboxProcessedTotal = meter.CreateCounter<long>(
            "fellowcore_notification_outbox_processed_total",
            description: "Total notification outbox messages materializadas com sucesso em Notifications");

        _notificationOutboxFailedTotal = meter.CreateCounter<long>(
            "fellowcore_notification_outbox_failed_total",
            description: "Total falhas (transientes) processando notification outbox messages — agendam retry com backoff");

        _notificationOutboxDeadLetterTotal = meter.CreateCounter<long>(
            "fellowcore_notification_outbox_dead_letter_total",
            description: "Total notification outbox messages que excederam max attempts (DLQ) — investigação manual");

        _notificationOutboxPending = meter.CreateObservableGauge(
            "fellowcore_notification_outbox_pending",
            () => _notificationOutboxPendingValue,
            description: "Mensagens pendentes (ProcessedAt IS NULL) no notification outbox neste momento");

        _reconciliationIssuesTotal = meter.CreateObservableGauge(
            "fellowcore_reconciliation_issues_total",
            () => ObserveReconciliationIssues(),
            description: "Total open reconciliation issues by severity");

        _ledgerGlobalImbalanceCents = meter.CreateObservableGauge(
            "fellowcore_ledger_global_imbalance_cents",
            () => _ledgerGlobalImbalanceCentsValue,
            unit: "cents",
            description: "Global ledger imbalance in cents (should be 0)");

        _splitDistributionsTotal = meter.CreateCounter<long>(
            "fellowcore_split_distributions_total",
            description: "Total number of split distributions from clearing");

        _splitClearingBalanceCents = meter.CreateObservableGauge(
            "fellowcore_split_clearing_balance_cents",
            () => _splitClearingBalanceCentsValue,
            unit: "cents",
            description: "Current SPLIT_CLEARING balance in cents (should trend toward 0)");

        _platformMarginCents = meter.CreateObservableGauge(
            "fellowcore_platform_margin_cents",
            () => _platformMarginCentsValue,
            unit: "cents",
            description: "Current PLATFORM_MARGIN balance in cents (negative means subsidy)");

        _disputesOpenCount = meter.CreateObservableGauge(
            "fellowcore_disputes_open_count",
            () => _disputesOpenCountValue,
            description: "Current number of open disputes");

        _hangfireLastHeartbeatSeconds = meter.CreateObservableGauge(
            "fellowcore_hangfire_last_heartbeat_seconds",
            () => _hangfireLastHeartbeatSecondsValue,
            unit: "s",
            description: "Seconds since last Hangfire worker heartbeat");

        _hangfireFailedCount = meter.CreateObservableGauge(
            "fellowcore_hangfire_failed_count",
            () => _hangfireFailedCountValue,
            description: "Current number of failed Hangfire jobs");

        _healthCheckStatus = meter.CreateObservableGauge(
            "fellowcore_health_check_status",
            () => ObserveHealthCheckStatuses(),
            description: "Health check status: 1=healthy, 0=unhealthy");

        _passwordResetTotal = meter.CreateCounter<long>(
            "fellowcore_password_reset_total",
            description: "Total password reset operations by result (sent, ignored_inactive, ignored_not_found, invalid_token, expired_token, success)");

        // Hybrid Settlement (ADVANCE mode) — drives cash flow risk monitoring.
        // Spike em advance_advanced_amount_cents sem reserva de caixa equivalente
        // = alerta operacional. Telemetria pra evoluir o item #1 (reserva de caixa).
        _advanceCapturedTotal = meter.CreateCounter<long>(
            "fellowcore_advance_captured_total",
            description: "Total transactions captured in ADVANCE mode (platform fronting cash)");

        _advanceFeeCollectedCents = meter.CreateCounter<long>(
            "fellowcore_advance_fee_collected_cents",
            unit: "cents",
            description: "Cumulative advance fee collected to PLATFORM_MARGIN (in cents)");

        _advanceAdvancedAmountCents = meter.CreateCounter<long>(
            "fellowcore_advance_advanced_amount_cents",
            unit: "cents",
            description: "Cumulative net amount advanced to sellers via ADVANCE mode (in cents). Cash flow exposure indicator.");

        _advanceReversedTotal = meter.CreateCounter<long>(
            "fellowcore_advance_reversed_total",
            description: "Total ADVANCE reversals due to refund/dispute lost — platform absorbs fee loss");

        _advanceThrottledTotal = meter.CreateCounter<long>(
            "fellowcore_advance_throttled_total",
            description: "Total ADVANCE requests blocked by anti-fraude rules (fallback to INSTALLMENT). Labeled by reason.");

        _tierDiscountAppliedTotal = meter.CreateCounter<long>(
            "fellowcore_tier_discount_applied_total",
            description: "Total TX that received a tier-based discount on platform fee. Labeled by tier.");

        _advanceReserveRemainingCents = meter.CreateObservableGauge(
            "fellowcore_advance_reserve_remaining_cents",
            () => _advanceReserveRemainingCentsValue,
            unit: "cents",
            description: "Current ADVANCE reserve balance across tenants (cents). Sum of TenantConfig.PlatformAdvanceReserveCents.");

        _advanceReserveCapacityCents = meter.CreateObservableGauge(
            "fellowcore_advance_reserve_capacity_cents",
            () => _advanceReserveCapacityCentsValue,
            unit: "cents",
            description: "Peak ADVANCE reserve capacity ever held (cents). Used by AdvanceReserveLow alert to compute pct_used.");

        _sellerExposureCurrentCents = meter.CreateObservableGauge(
            "fellowcore_seller_exposure_current_cents",
            () => ObserveSellerExposures(),
            unit: "cents",
            description: "Per-seller current ADVANCE exposure in cents. Labeled by seller_id.");

        _sellerExposureThresholdCents = meter.CreateObservableGauge(
            "fellowcore_seller_exposure_threshold_cents",
            () => ObserveSellerThresholds(),
            unit: "cents",
            description: "Per-seller alert threshold in cents. Drives AdvanceSellerExposureHigh alert (exposure > threshold).");
    }

    private IEnumerable<Measurement<long>> ObserveSellerThresholds()
    {
        lock (_sellerExposureThresholdsCents)
        {
            foreach (var kvp in _sellerExposureThresholdsCents)
            {
                yield return new Measurement<long>(kvp.Value,
                    new KeyValuePair<string, object?>("seller_id", kvp.Key));
            }
        }
    }

    private IEnumerable<Measurement<long>> ObserveSellerExposures()
    {
        lock (_sellerExposureCents)
        {
            foreach (var kvp in _sellerExposureCents)
            {
                yield return new Measurement<long>(kvp.Value,
                    new KeyValuePair<string, object?>("seller_id", kvp.Key));
            }
        }
    }

    /// <summary>Atualiza gauges agregados pelo MetricsCollectorWorker.</summary>
    public void SetAdvanceReserveValues(long remainingCents, long capacityCents)
    {
        _advanceReserveRemainingCentsValue = remainingCents;
        // Capacity é monotonicamente crescente — preserva o pico histórico
        if (capacityCents > _advanceReserveCapacityCentsValue)
            _advanceReserveCapacityCentsValue = capacityCents;
    }

    /// <summary>Snapshot do exposure per-seller pra emissão das series.</summary>
    public void SetSellerExposure(string sellerId, long exposureCents)
    {
        lock (_sellerExposureCents)
        {
            if (exposureCents > 0)
                _sellerExposureCents[sellerId] = exposureCents;
            else
                _sellerExposureCents.Remove(sellerId);
        }
    }

    /// <summary>Snapshot do threshold per-seller (custom ou default global).</summary>
    public void SetSellerExposureThreshold(string sellerId, long thresholdCents)
    {
        lock (_sellerExposureThresholdsCents)
        {
            _sellerExposureThresholdsCents[sellerId] = thresholdCents;
        }
    }

    // --- Hybrid Settlement (ADVANCE mode) ---
    public void RecordAdvanceCapture(decimal netAdvancedToSeller, decimal feeCollected)
    {
        _advanceCapturedTotal.Add(1);
        _advanceFeeCollectedCents.Add((long)Math.Round(feeCollected * 100m));
        _advanceAdvancedAmountCents.Add((long)Math.Round(netAdvancedToSeller * 100m));
    }

    public void RecordAdvanceReversal()
    {
        _advanceReversedTotal.Add(1);
    }

    public void RecordAdvanceThrottled(string reason)
    {
        _advanceThrottledTotal.Add(1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordTierDiscountApplied(string tier)
    {
        _tierDiscountAppliedTotal.Add(1,
            new KeyValuePair<string, object?>("tier", tier));
    }

    // --- Transaction ---
    public void RecordTransaction(string status, string paymentType, string provider)
    {
        _transactionsTotal.Add(1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("payment_type", paymentType),
            new KeyValuePair<string, object?>("provider", provider));
    }

    public void RecordRefund()
    {
        _refundsTotal.Add(1);
    }

    // --- Payouts ---
    public void RecordPayout(string status)
    {
        _payoutsTotal.Add(1,
            new KeyValuePair<string, object?>("status", status));
    }

    public void RecordPayoutFailed()
    {
        _payoutsFailedTotal.Add(1);
    }

    public void RecordPayoutRetry()
    {
        _payoutsRetryTotal.Add(1);
    }

    public void RecordRefundRetry()
    {
        _refundsRetryTotal.Add(1);
    }

    public void RecordZombieCancellation(long count)
    {
        if (count > 0) _zombieCancellationsTotal.Add(count);
    }

    public void SetPayoutsPendingAgeHours(double hours)
    {
        _payoutsPendingAgeHoursValue = hours;
    }

    // --- Provider ---
    public void RecordProviderRequestDuration(double durationSeconds, string provider, string operation)
    {
        _providerRequestDuration.Record(durationSeconds,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("operation", operation));
    }

    public void RecordProviderRequest(string provider)
    {
        _providerRequestsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", provider));
    }

    public void RecordProviderError(string provider, string errorType)
    {
        _providerErrorsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("error_type", errorType));
    }

    public void SetCircuitBreakerState(string provider, int state)
    {
        lock (_circuitBreakerStates)
        {
            _circuitBreakerStates[provider] = state;
        }
    }

    // --- Webhooks ---
    public void RecordWebhookDelivery(string status)
    {
        _webhookDeliveriesTotal.Add(1,
            new KeyValuePair<string, object?>("status", status));
    }

    public void RecordWebhookDeliveryFailure()
    {
        _webhookDeliveryFailuresTotal.Add(1);
    }

    public void RecordWebhookDuplicate()
    {
        _webhookDuplicateTotal.Add(1);
    }

    public void RecordWebhookInvalidSignature()
    {
        _webhookInvalidSignatureTotal.Add(1);
    }

    // --- Operational ---
    public void RecordSplitDistributeIdempotencyHit()
    {
        _splitDistributeIdempotencyHitTotal.Add(1);
    }

    public void RecordProviderCostMismatch()
    {
        _providerCostMismatchTotal.Add(1);
    }

    public void RecordPlatformMarginNegative()
    {
        _platformMarginNegativeTotal.Add(1);
    }

    // --- Auth ---
    public void RecordPasswordReset(string result)
    {
        _passwordResetTotal.Add(1,
            new KeyValuePair<string, object?>("result", result));
    }

    // --- Outbox ---
    public void SetOutboxDeadLetterCount(long count)
    {
        _outboxDeadLetterCountValue = count;
    }

    // --- Notification outbox (Sprint 2) ---
    public void RecordNotificationOutboxProcessed(long count = 1)
        => _notificationOutboxProcessedTotal.Add(count);

    public void RecordNotificationOutboxFailed(long count = 1)
        => _notificationOutboxFailedTotal.Add(count);

    public void RecordNotificationOutboxDeadLetter(long count = 1)
        => _notificationOutboxDeadLetterTotal.Add(count);

    public void SetNotificationOutboxPending(long count)
        => _notificationOutboxPendingValue = count;

    // --- Reconciliation / Ledger ---
    public void SetReconciliationIssues(string severity, long count)
    {
        lock (_reconciliationIssuesBySeverity)
        {
            _reconciliationIssuesBySeverity[severity] = count;
        }
    }

    public void SetLedgerGlobalImbalanceCents(double cents)
    {
        _ledgerGlobalImbalanceCentsValue = cents;
    }

    // --- Split/Clearing ---
    public void RecordSplitDistribution(string status)
    {
        _splitDistributionsTotal.Add(1,
            new KeyValuePair<string, object?>("status", status));
    }

    public void SetSplitClearingBalanceCents(double cents)
    {
        _splitClearingBalanceCentsValue = cents;
    }

    public void SetPlatformMarginCents(double cents)
    {
        _platformMarginCentsValue = cents;
    }

    // --- Disputes ---
    public void SetDisputesOpenCount(long count)
    {
        _disputesOpenCountValue = count;
    }

    // --- Hangfire ---
    public void SetHangfireLastHeartbeatSeconds(double seconds)
    {
        _hangfireLastHeartbeatSecondsValue = seconds;
    }

    public void SetHangfireFailedCount(long count)
    {
        _hangfireFailedCountValue = count;
    }

    // --- Health Checks ---
    public void SetHealthCheckStatus(string component, bool healthy)
    {
        lock (_healthCheckStatuses)
        {
            _healthCheckStatuses[component] = healthy ? 1 : 0;
        }
    }

    // --- Observable gauge callbacks ---
    private IEnumerable<Measurement<int>> ObserveCircuitBreakerStates()
    {
        lock (_circuitBreakerStates)
        {
            foreach (var (provider, state) in _circuitBreakerStates)
            {
                yield return new Measurement<int>(state,
                    new KeyValuePair<string, object?>("provider", provider));
            }
        }
    }

    private IEnumerable<Measurement<long>> ObserveReconciliationIssues()
    {
        lock (_reconciliationIssuesBySeverity)
        {
            foreach (var (severity, count) in _reconciliationIssuesBySeverity)
            {
                yield return new Measurement<long>(count,
                    new KeyValuePair<string, object?>("severity", severity));
            }
        }
    }

    private IEnumerable<Measurement<int>> ObserveHealthCheckStatuses()
    {
        lock (_healthCheckStatuses)
        {
            foreach (var (component, status) in _healthCheckStatuses)
            {
                yield return new Measurement<int>(status,
                    new KeyValuePair<string, object?>("component", component));
            }
        }
    }
}
