using FellowCore.Application.Common.Interfaces;

namespace FellowCore.Api.Metrics;

/// <summary>
/// Bridges IAppMetrics (application layer) to FellowCoreMetrics (OpenTelemetry instruments).
/// </summary>
public sealed class AppMetricsAdapter(FellowCoreMetrics metrics) : IAppMetrics
{
    public void RecordTransaction(string status, string paymentType, string provider)
        => metrics.RecordTransaction(status, paymentType, provider);

    public void RecordRefund()
        => metrics.RecordRefund();

    public void RecordPayout(string status)
        => metrics.RecordPayout(status);

    public void RecordPayoutFailed()
        => metrics.RecordPayoutFailed();

    public void RecordPayoutRetry()
        => metrics.RecordPayoutRetry();

    public void RecordRefundRetry()
        => metrics.RecordRefundRetry();

    public void RecordZombieCancellation(int count)
        => metrics.RecordZombieCancellation(count);

    public void RecordProviderRequest(string provider)
        => metrics.RecordProviderRequest(provider);

    public void RecordProviderError(string provider, string errorType)
        => metrics.RecordProviderError(provider, errorType);

    public void RecordProviderRequestDuration(double durationSeconds, string provider, string operation)
        => metrics.RecordProviderRequestDuration(durationSeconds, provider, operation);

    public void RecordWebhookDelivery(string status)
        => metrics.RecordWebhookDelivery(status);

    public void RecordWebhookDeliveryFailure()
        => metrics.RecordWebhookDeliveryFailure();

    public void RecordWebhookDuplicate()
        => metrics.RecordWebhookDuplicate();

    public void RecordWebhookInvalidSignature()
        => metrics.RecordWebhookInvalidSignature();

    public void RecordSplitDistributeIdempotencyHit()
        => metrics.RecordSplitDistributeIdempotencyHit();

    public void RecordProviderCostMismatch()
        => metrics.RecordProviderCostMismatch();

    public void RecordPlatformMarginNegative()
        => metrics.RecordPlatformMarginNegative();

    public void SetCircuitBreakerState(string provider, int state)
        => metrics.SetCircuitBreakerState(provider, state);

    public void RecordPasswordReset(string result)
        => metrics.RecordPasswordReset(result);

    public void RecordAdvanceCapture(decimal netAdvancedToSeller, decimal feeCollected)
        => metrics.RecordAdvanceCapture(netAdvancedToSeller, feeCollected);

    public void RecordAdvanceReversal()
        => metrics.RecordAdvanceReversal();

    public void RecordAdvanceThrottled(string reason)
        => metrics.RecordAdvanceThrottled(reason);

    public void RecordTierDiscountApplied(string tier)
        => metrics.RecordTierDiscountApplied(tier);

    public void RecordNotificationOutboxProcessed(long count = 1)
        => metrics.RecordNotificationOutboxProcessed(count);

    public void RecordNotificationOutboxFailed(long count = 1)
        => metrics.RecordNotificationOutboxFailed(count);

    public void RecordNotificationOutboxDeadLetter(long count = 1)
        => metrics.RecordNotificationOutboxDeadLetter(count);

    public void SetNotificationOutboxPending(long count)
        => metrics.SetNotificationOutboxPending(count);
}
