namespace FellowCore.Application.Common.Interfaces;

/// <summary>
/// Abstraction for application-level metrics emitted by services and processors.
/// Implemented in the API layer (FellowCoreMetrics adapter).
/// </summary>
public interface IAppMetrics
{
    void RecordTransaction(string status, string paymentType, string provider);
    void RecordRefund();
    void RecordPayout(string status);
    void RecordPayoutFailed();
    void RecordPayoutRetry();
    void RecordRefundRetry();
    void RecordProviderRequest(string provider);
    void RecordProviderError(string provider, string errorType);
    void RecordProviderRequestDuration(double durationSeconds, string provider, string operation);
    void RecordWebhookDelivery(string status);
    void RecordWebhookDeliveryFailure();
    void RecordWebhookDuplicate();
    void RecordWebhookInvalidSignature();
    void RecordSplitDistributeIdempotencyHit();
    void RecordProviderCostMismatch();
    void RecordPlatformMarginNegative();
    void SetCircuitBreakerState(string provider, int state);
    void RecordPasswordReset(string result);
    /// <summary>
    /// Conta TXs canceladas pelo cleanup de zumbis (CREATED/PROCESSING velhos
    /// que nunca foram completados). Útil pra monitorar saúde do funil de
    /// conversão e detectar bug em providers (ex: spike de zumbis = webhooks
    /// perdidos).
    /// </summary>
    void RecordZombieCancellation(int count);

    /// <summary>
    /// Captura de TX em modo ADVANCE (Modelo Híbrido — antecipação automática).
    /// Drives observability do risco de fluxo de caixa: <c>netAdvancedToSeller</c>
    /// é o valor que a plataforma adianta antes da Stripe liberar (gap a cobrir
    /// com caixa próprio). <c>feeCollected</c> é a receita extra que entra em
    /// PLATFORM_MARGIN como compensação do risco.
    /// </summary>
    void RecordAdvanceCapture(decimal netAdvancedToSeller, decimal feeCollected);

    /// <summary>
    /// Reversão de ADVANCE em refund/dispute lost. Spike aqui = plataforma está
    /// perdendo o fee de antecipações canceladas — sinaliza necessidade de revisão
    /// na elegibilidade pra antecipar (anti-fraude, scoring de seller, etc.).
    /// </summary>
    void RecordAdvanceReversal();

    /// <summary>
    /// TX que pediu ADVANCE foi bloqueada pelo anti-fraude e caiu pra INSTALLMENT.
    /// <c>reason</c> é o BlockReason vindo do <c>AdvanceRiskEvaluator</c>
    /// (seller_too_new, high_risk_score, etc.). Spike por razão específica =
    /// regra calibrada pra estritamente ou ataque em curso.
    /// </summary>
    void RecordAdvanceThrottled(string reason);

    /// <summary>
    /// TX que recebeu desconto por tier (Sprint 1 #3). Label <c>tier</c> permite
    /// dashboards "quanto de receita potencial estamos abrindo mão por tier" —
    /// importante pra calibrar a tabela de discount sem destruir margem.
    /// Não chamado quando o seller é SILVER (sem desconto) ou sem profile.
    /// </summary>
    void RecordTierDiscountApplied(string tier);

    /// <summary>
    /// Notification outbox (Sprint 2). Cada batch do <c>NotificationOutboxProcessor</c>
    /// reporta:
    ///  - <c>RecordNotificationOutboxProcessed</c>: counter de materializadas com sucesso
    ///  - <c>RecordNotificationOutboxFailed</c>: counter de falhas transientes (retry agendado)
    ///  - <c>RecordNotificationOutboxDeadLetter</c>: counter de DLQ (max attempts excedido)
    ///  - <c>SetNotificationOutboxPending</c>: gauge do backlog atual após processar
    /// </summary>
    void RecordNotificationOutboxProcessed(long count = 1);
    void RecordNotificationOutboxFailed(long count = 1);
    void RecordNotificationOutboxDeadLetter(long count = 1);
    void SetNotificationOutboxPending(long count);
}
