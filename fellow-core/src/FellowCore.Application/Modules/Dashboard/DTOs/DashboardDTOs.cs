using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Dashboard.DTOs;

public record DashboardFilterDto(
    DateTime? From = null,
    DateTime? To = null,
    Guid? SellerId = null,
    PaymentProvider? Provider = null
);

public record DashboardDto(
    decimal TotalVolume,
    decimal TotalFees,
    decimal TotalNet,
    decimal TotalPlatformFees,
    decimal TotalProviderCosts,
    decimal TotalPlatformMargin,
    decimal MarginPercent,
    int TransactionCount,
    // Métricas restritas a transações capturadas (dinheiro que efetivamente entrou).
    // O dashboard precisa diferenciar tentativas (TotalVolume) do que de fato foi
    // recebido (CapturedVolume) — sem isso, taxa de recusa contamina os KPIs.
    decimal CapturedVolume,
    decimal CapturedFees,
    decimal CapturedNet,
    int CapturedCount,
    List<StatusCountDto> ByStatus,
    List<PaymentTypeCountDto> ByPaymentType,
    List<ProviderVolumeDto> ByProvider,
    // Cross-tab (status × paymentType) pra widgets que precisam saber, por
    // exemplo, "quantas transações pendentes são PIX vs Boleto vs Cartão".
    // Lista de tuplas — frontend filtra por status set conforme a métrica.
    List<StatusMethodCountDto> ByStatusAndMethod
);

public record StatusCountDto(TransactionStatus Status, int Count, decimal Volume);
public record PaymentTypeCountDto(PaymentType PaymentType, int Count, decimal Volume);
public record StatusMethodCountDto(TransactionStatus Status, PaymentType PaymentType, int Count, decimal Volume);

public record ProviderVolumeDto(PaymentProvider Provider, int Count, decimal Volume, decimal PlatformFees, decimal ProviderCosts, decimal Margin);

public enum DashboardGranularity { Day, Week, Hour }

public record DashboardTimeseriesPointDto(DateTime Date, decimal Volume, decimal Net, decimal Fees, decimal Margin, int Count);

public record DashboardTimeseriesDto(
    DashboardGranularity Granularity,
    DateTime From,
    DateTime To,
    List<DashboardTimeseriesPointDto> Points
);

/// <summary>
/// Uma célula do heatmap dia × hora. DayOfWeek 0=Domingo .. 6=Sábado (alinhado com .NET).
/// Hour 0..23. Count é o número de transações capturadas naquele bucket.
/// </summary>
public record DashboardHeatmapCellDto(int DayOfWeek, int Hour, int Count, decimal Volume);

/// <summary>
/// Funil de conversão por método de pagamento. Captured = capturadas (dinheiro entrou),
/// Pending = ainda em CREATED/PROCESSING/AUTHORIZED, Declined = DECLINED/FAILED/VOIDED.
/// ApprovalRate = Captured / Total. Permite identificar método que vaza mais.
/// </summary>
public record ConversionByMethodDto(
    PaymentType PaymentType,
    int Total,
    int Captured,
    int Pending,
    int Declined,
    decimal CapturedVolume,
    double ApprovalRate
);

/// <summary>
/// Bin de histograma de valores. MinAmount inclusivo, MaxAmount exclusivo
/// (null = sem limite superior). Label é a legenda renderizada (ex: "R$ 50 – R$ 200").
/// </summary>
public record TicketDistributionBinDto(string Label, decimal MinAmount, decimal? MaxAmount, int Count, decimal Volume);

public record TicketDistributionDto(int TotalCount, decimal AverageTicket, decimal MedianTicket, List<TicketDistributionBinDto> Bins);

/// <summary>
/// Métrica de retenção simples para o período: clientes únicos identificáveis
/// (PayerEmail), quantos haviam comprado antes do início do período (returning),
/// e a taxa de retorno. RepeatInPeriod = clientes que fizeram >1 compra dentro do período.
/// </summary>
public record CustomerRetentionDto(
    int UniqueCustomers,
    int ReturningCustomers,
    int NewCustomers,
    int RepeatInPeriod,
    double ReturningRate,
    double RepeatInPeriodRate
);

public record DashboardHeatmapDto(DateTime From, DateTime To, List<DashboardHeatmapCellDto> Cells);

public record TopCustomerDto(string Email, string? Name, int Count, decimal Volume);

public record TopPaymentLinkDto(Guid PaymentLinkId, string Name, string Token, int Count, decimal Volume);

/// <summary>
/// Produto mais vendido no período. Resolve via <c>Transaction.ExternalReferenceId</c>
/// com formato <c>product:{guid}</c> — mesmo formato emitido pelo
/// MarketplaceCheckoutService e já usado por <c>ProductRepository.GetMetricsForProductsAsync</c>.
/// Bumps (<c>bump:{guid}</c> em <c>TransactionItem.ProductId</c>) ficam fora dessa
/// agregação — entrariam num "Top bumps" separado se um dia for útil.
/// </summary>
public record TopProductDto(
    Guid ProductId,
    string Name,
    string? Slug,
    string? CoverImageUrl,
    int Count,
    decimal Volume);

public record FinancialHealthDto(
    int LedgerImbalanceCount,
    long? PlatformDriftCents,
    string? LastReconciliationStatus,
    int PendingPayoutsCount,
    decimal PendingPayoutsTotal,
    int FailedWebhooksLast24h,
    int OpenDisputesCount,
    decimal DisputeExposureAmount,
    int SplitsPendingCount,
    int SplitsFailedCount,
    int ReconciliationIssuesOpen
);
