using FellowCore.Application.Modules.Dashboard.DTOs;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Dashboard.Services;

public interface IDashboardService
{
    Task<DashboardDto> GetSummaryAsync(Guid tenantId, DashboardFilterDto filter);
    Task<FinancialHealthDto> GetFinancialHealthAsync(Guid tenantId);
    Task<DashboardTimeseriesDto> GetTimeseriesAsync(Guid tenantId, DashboardFilterDto filter, DashboardGranularity? granularity);
    Task<List<TopCustomerDto>> GetTopCustomersAsync(Guid tenantId, DashboardFilterDto filter, int limit);
    Task<List<TopPaymentLinkDto>> GetTopPaymentLinksAsync(Guid tenantId, DashboardFilterDto filter, int limit);
    Task<List<TopProductDto>> GetTopProductsAsync(Guid tenantId, DashboardFilterDto filter, int limit);
    Task<DashboardHeatmapDto> GetHeatmapAsync(Guid tenantId, DashboardFilterDto filter);
    Task<List<ConversionByMethodDto>> GetConversionByMethodAsync(Guid tenantId, DashboardFilterDto filter);
    Task<TicketDistributionDto> GetTicketDistributionAsync(Guid tenantId, DashboardFilterDto filter);
    Task<CustomerRetentionDto> GetCustomerRetentionAsync(Guid tenantId, DashboardFilterDto filter);
}

public class DashboardService(
    ITransactionRepository transactionRepository,
    IReconciliationRepository reconciliationRepository,
    IPayoutRepository payoutRepository,
    IWebhookDeliveryRepository webhookDeliveryRepository,
    IDisputeRepository disputeRepository,
    ISplitTransferRepository splitTransferRepository,
    IPaymentLinkRepository paymentLinkRepository,
    IProductRepository productRepository) : IDashboardService
{
    public async Task<DashboardDto> GetSummaryAsync(Guid tenantId, DashboardFilterDto filter)
    {
        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, filter.From, filter.To, filter.SellerId, filter.Provider);

        // ByStatus continua sobre TUDO — é o widget "Transações por Status" que serve
        // justamente pra mostrar a distribuição (capturadas, recusadas, pendentes etc).
        var byStatus = transactions
            .GroupBy(t => t.Status)
            .Select(g => new StatusCountDto(g.Key, g.Count(), g.Sum(t => t.Amount)))
            .ToList();

        // Cross-tab status × método — usado pelo PendingFundsCard pra mostrar
        // "30 PIX + 8 boletos pendentes", por exemplo.
        var byStatusAndMethod = transactions
            .GroupBy(t => new { t.Status, t.PaymentType })
            .Select(g => new StatusMethodCountDto(g.Key.Status, g.Key.PaymentType, g.Count(), g.Sum(t => t.Amount)))
            .ToList();

        // Para todo o resto, "volume" e "receita" significam transações capturadas.
        // Tentativas recusadas/pendentes não são receita e não devem inflar os
        // breakdowns por método/provider, nem o totalizador da margem.
        var capturedTx = transactions.Where(t => t.Status == TransactionStatus.CAPTURED).ToList();

        var byPaymentType = capturedTx
            .GroupBy(t => t.PaymentType)
            .Select(g => new PaymentTypeCountDto(g.Key, g.Count(), g.Sum(t => t.Amount)))
            .ToList();

        var byProvider = capturedTx
            .GroupBy(t => t.Provider)
            .Select(g => new ProviderVolumeDto(
                g.Key, g.Count(), g.Sum(t => t.Amount),
                g.Sum(t => t.PlatformFeeAmount ?? 0),
                g.Sum(t => t.ProviderCostAmount ?? 0),
                g.Sum(t => t.PlatformMarginAmount ?? 0)))
            .ToList();

        decimal capturedVolume = capturedTx.Sum(t => t.Amount);
        decimal capturedFees = capturedTx.Sum(t => t.FeeAmount ?? 0);
        decimal capturedNet = capturedTx.Sum(t => t.NetAmount ?? 0);
        decimal capturedPlatformFees = capturedTx.Sum(t => t.PlatformFeeAmount ?? 0);
        decimal capturedProviderCosts = capturedTx.Sum(t => t.ProviderCostAmount ?? 0);
        decimal capturedMargin = capturedTx.Sum(t => t.PlatformMarginAmount ?? 0);
        decimal marginPercent = capturedVolume > 0 ? Math.Round(capturedMargin / capturedVolume * 100, 2) : 0;

        // Os campos legados (TotalVolume/TotalFees/TotalNet/TransactionCount) ainda
        // refletem TUDO — alguns lugares do app dependem disso e a quebra silenciosa
        // seria pior. Cards do dashboard usam os Captured*.
        return new DashboardDto(
            TotalVolume: transactions.Sum(t => t.Amount),
            TotalFees: transactions.Sum(t => t.FeeAmount ?? 0),
            TotalNet: transactions.Sum(t => t.NetAmount ?? 0),
            TotalPlatformFees: capturedPlatformFees,
            TotalProviderCosts: capturedProviderCosts,
            TotalPlatformMargin: capturedMargin,
            MarginPercent: marginPercent,
            TransactionCount: transactions.Count,
            CapturedVolume: capturedVolume,
            CapturedFees: capturedFees,
            CapturedNet: capturedNet,
            CapturedCount: capturedTx.Count,
            ByStatus: byStatus,
            ByPaymentType: byPaymentType,
            ByProvider: byProvider,
            ByStatusAndMethod: byStatusAndMethod
        );
    }

    public async Task<DashboardTimeseriesDto> GetTimeseriesAsync(Guid tenantId, DashboardFilterDto filter, DashboardGranularity? granularity)
    {
        // Default range: últimos 30 dias se nada vier. Mantém consistência com a UI.
        DateTime to = filter.To ?? DateTime.UtcNow;
        DateTime from = filter.From ?? to.AddDays(-29);

        // Se a granularidade não vier, escolhemos pela duração: ≤24h hora, ≤60d dia, senão semana.
        DashboardGranularity gran = granularity ?? (
            (to - from).TotalHours <= 24 ? DashboardGranularity.Hour :
            (to - from).TotalDays > 60 ? DashboardGranularity.Week :
            DashboardGranularity.Day);

        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, from, to, filter.SellerId, filter.Provider);

        // Timeseries do dashboard reflete dinheiro que entrou no período — só capturadas.
        // Tentativas recusadas/pendentes não fazem parte da curva de receita.
        var capturedTx = transactions.Where(t => t.Status == TransactionStatus.CAPTURED).ToList();

        // Truncamento de bucket por granularidade: hora, dia ou início da semana (segunda).
        DateTime BucketStart(DateTime t)
        {
            DateTime utc = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
            if (gran == DashboardGranularity.Hour)
                return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
            DateTime day = new(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
            if (gran == DashboardGranularity.Day) return day;
            int diff = ((int)day.DayOfWeek + 6) % 7; // segunda = 0
            return day.AddDays(-diff);
        }

        var grouped = capturedTx
            .GroupBy(t => BucketStart(t.CreatedAt))
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => new
            {
                Volume = g.Sum(t => t.Amount),
                Net = g.Sum(t => t.NetAmount ?? 0),
                Fees = g.Sum(t => t.FeeAmount ?? 0),
                Margin = g.Sum(t => t.PlatformMarginAmount ?? 0),
                Count = g.Count(),
            });

        // Preenchimento de buckets vazios — gráfico ininterrupto. Sem isso, dias sem
        // venda viram um "buraco" na linha que confunde a leitura visual.
        DateTime cursor = BucketStart(from);
        DateTime end = BucketStart(to);
        TimeSpan step = gran switch
        {
            DashboardGranularity.Hour => TimeSpan.FromHours(1),
            DashboardGranularity.Week => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(1),
        };

        var points = new List<DashboardTimeseriesPointDto>();
        while (cursor <= end)
        {
            if (grouped.TryGetValue(cursor, out var bucket))
                points.Add(new DashboardTimeseriesPointDto(cursor, bucket.Volume, bucket.Net, bucket.Fees, bucket.Margin, bucket.Count));
            else
                points.Add(new DashboardTimeseriesPointDto(cursor, 0m, 0m, 0m, 0m, 0));
            cursor = cursor.Add(step);
        }

        return new DashboardTimeseriesDto(gran, from, to, points);
    }

    public async Task<TicketDistributionDto> GetTicketDistributionAsync(Guid tenantId, DashboardFilterDto filter)
    {
        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, filter.From, filter.To, filter.SellerId, filter.Provider);
        // Distribuição leva só capturadas — tickets de tentativas recusadas distorcem a visão
        // do "tamanho médio do pagamento que entrou".
        var captured = transactions.Where(t => t.Status == TransactionStatus.CAPTURED).ToList();

        // Bins fixos pra MVP. Pra Fellow Pay (cobranças tipicamente <R$1000), cobre bem.
        // Caso o seller tenha tickets muito altos (B2B), 1k+ acumula tudo no último bin.
        var binDefs = new (string Label, decimal Min, decimal? Max)[]
        {
            ("R$ 0 – 50",       0m,    50m),
            ("R$ 50 – 200",     50m,   200m),
            ("R$ 200 – 500",    200m,  500m),
            ("R$ 500 – 1k",     500m,  1000m),
            ("R$ 1k – 5k",      1000m, 5000m),
            ("R$ 5k+",          5000m, null),
        };

        var bins = binDefs.Select(def =>
        {
            var inBin = captured.Where(t =>
                t.Amount >= def.Min && (def.Max == null || t.Amount < def.Max));
            return new TicketDistributionBinDto(
                Label: def.Label,
                MinAmount: def.Min,
                MaxAmount: def.Max,
                Count: inBin.Count(),
                Volume: inBin.Sum(t => t.Amount));
        }).ToList();

        decimal avg = captured.Count > 0 ? captured.Sum(t => t.Amount) / captured.Count : 0;
        decimal median = 0;
        if (captured.Count > 0)
        {
            var sorted = captured.Select(t => t.Amount).OrderBy(a => a).ToList();
            int mid = sorted.Count / 2;
            median = sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
        }

        return new TicketDistributionDto(captured.Count, avg, median, bins);
    }

    public async Task<CustomerRetentionDto> GetCustomerRetentionAsync(Guid tenantId, DashboardFilterDto filter)
    {
        // Clientes "in-period": transações capturadas dentro da janela.
        var inPeriod = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, filter.From, filter.To, filter.SellerId, filter.Provider);
        var capturedInPeriod = inPeriod.Where(t => t.Status == TransactionStatus.CAPTURED && !string.IsNullOrWhiteSpace(t.PayerEmail)).ToList();

        // Agrupa por email pra ter clientes únicos dentro do período.
        var emailsInPeriodGroups = capturedInPeriod
            .GroupBy(t => t.PayerEmail!.Trim().ToLowerInvariant())
            .ToList();

        int uniqueCustomers = emailsInPeriodGroups.Count;
        int repeatInPeriod = emailsInPeriodGroups.Count(g => g.Count() > 1);

        // Pra "returning" precisamos saber se cada email comprou antes do `From`.
        // Buscamos só capturas anteriores — janela de 1 ano antes pra não escanear DB inteiro.
        DateTime cutoffStart = (filter.From ?? DateTime.UtcNow.AddDays(-30)).AddYears(-1);
        DateTime cutoffEnd = filter.From ?? DateTime.UtcNow.AddDays(-30);
        var historic = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, cutoffStart, cutoffEnd, filter.SellerId, filter.Provider);
        var historicEmails = historic
            .Where(t => t.Status == TransactionStatus.CAPTURED && !string.IsNullOrWhiteSpace(t.PayerEmail))
            .Select(t => t.PayerEmail!.Trim().ToLowerInvariant())
            .ToHashSet();

        int returningCustomers = emailsInPeriodGroups.Count(g => historicEmails.Contains(g.Key));
        int newCustomers = uniqueCustomers - returningCustomers;

        double returningRate = uniqueCustomers > 0 ? Math.Round((double)returningCustomers / uniqueCustomers * 100, 2) : 0;
        double repeatRate = uniqueCustomers > 0 ? Math.Round((double)repeatInPeriod / uniqueCustomers * 100, 2) : 0;

        return new CustomerRetentionDto(
            UniqueCustomers: uniqueCustomers,
            ReturningCustomers: returningCustomers,
            NewCustomers: newCustomers,
            RepeatInPeriod: repeatInPeriod,
            ReturningRate: returningRate,
            RepeatInPeriodRate: repeatRate);
    }

    public async Task<List<ConversionByMethodDto>> GetConversionByMethodAsync(Guid tenantId, DashboardFilterDto filter)
    {
        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, filter.From, filter.To, filter.SellerId, filter.Provider);

        return transactions
            .GroupBy(t => t.PaymentType)
            .Select(g =>
            {
                int total = g.Count();
                int captured = g.Count(t => t.Status == TransactionStatus.CAPTURED);
                int declined = g.Count(t => t.Status == TransactionStatus.DECLINED || t.Status == TransactionStatus.FAILED || t.Status == TransactionStatus.VOIDED);
                int pending = total - captured - declined; // CREATED, PROCESSING, AUTHORIZED, REFUNDED, etc.
                decimal capturedVolume = g.Where(t => t.Status == TransactionStatus.CAPTURED).Sum(t => t.Amount);
                double approvalRate = total > 0 ? Math.Round((double)captured / total * 100, 2) : 0;

                return new ConversionByMethodDto(
                    PaymentType: g.Key,
                    Total: total,
                    Captured: captured,
                    Pending: pending,
                    Declined: declined,
                    CapturedVolume: capturedVolume,
                    ApprovalRate: approvalRate);
            })
            .OrderByDescending(x => x.Total)
            .ToList();
    }

    public async Task<DashboardHeatmapDto> GetHeatmapAsync(Guid tenantId, DashboardFilterDto filter)
    {
        // Default range: 30 dias se nada vier — mesma convenção do timeseries.
        DateTime to = filter.To ?? DateTime.UtcNow;
        DateTime from = filter.From ?? to.AddDays(-29);

        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, from, to, filter.SellerId, filter.Provider);

        // Apenas CAPTURED entra — heatmap mostra "quando dinheiro entra" não "quando o cliente
        // tentou pagar". Tentativas DECLINED não dizem nada sobre comportamento real do comprador.
        var captured = transactions.Where(t => t.Status == TransactionStatus.CAPTURED);

        // Agrupamento (DayOfWeek, Hour) usando UTC. Frontend converte pra timezone do browser
        // se necessário — pra MVP ficamos em UTC pra consistência com tudo o resto do dashboard.
        var cells = captured
            .GroupBy(t =>
            {
                var utc = t.CreatedAt.Kind == DateTimeKind.Utc ? t.CreatedAt : t.CreatedAt.ToUniversalTime();
                return new { Dow = (int)utc.DayOfWeek, Hour = utc.Hour };
            })
            .Select(g => new DashboardHeatmapCellDto(g.Key.Dow, g.Key.Hour, g.Count(), g.Sum(t => t.Amount)))
            .ToList();

        return new DashboardHeatmapDto(from, to, cells);
    }

    public async Task<List<TopCustomerDto>> GetTopCustomersAsync(Guid tenantId, DashboardFilterDto filter, int limit)
    {
        // Reaproveita o mesmo fetch do summary. Top customer = quem realmente pagou
        // — incluir tentativas recusadas inflaria a posição de quem só tentou várias vezes
        // sem completar pagamento. Apenas capturadas entram no ranking.
        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(tenantId, filter.From, filter.To, filter.SellerId, filter.Provider);

        return transactions
            .Where(t => t.Status == TransactionStatus.CAPTURED && !string.IsNullOrWhiteSpace(t.PayerEmail))
            .GroupBy(t => t.PayerEmail!.Trim().ToLowerInvariant())
            .Select(g => new TopCustomerDto(
                Email: g.Key,
                Name: g.Where(t => !string.IsNullOrWhiteSpace(t.PayerName)).Select(t => t.PayerName).FirstOrDefault(),
                Count: g.Count(),
                Volume: g.Sum(t => t.Amount)))
            .OrderByDescending(x => x.Volume)
            .Take(limit)
            .ToList();
    }

    public async Task<List<TopPaymentLinkDto>> GetTopPaymentLinksAsync(Guid tenantId, DashboardFilterDto filter, int limit)
    {
        var rows = await paymentLinkRepository.GetTopByVolumeAsync(tenantId, filter.From, filter.To, filter.SellerId, limit);
        return rows
            .Select(r => new TopPaymentLinkDto(r.LinkId, r.Name, r.Token, r.Count, r.Volume))
            .ToList();
    }

    public async Task<List<TopProductDto>> GetTopProductsAsync(Guid tenantId, DashboardFilterDto filter, int limit)
    {
        // Resolve produto via Transaction.ExternalReferenceId == "product:{guid}".
        // Mesmo padrão usado em ProductRepository.GetMetricsForProductsAsync — fonte
        // canônica de "qual produto gerou essa TX" pra checkouts do marketplace.
        // Order bumps (que vivem em TransactionItem.ProductId com prefixo "bump:")
        // ficam de fora; entrariam num "Top bumps" separado se um dia houver demanda.
        const string prefix = "product:";

        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(
            tenantId, filter.From, filter.To, filter.SellerId, filter.Provider);

        // 1) Filtra capturadas com ExternalReferenceId no formato esperado, parseia
        //    o Guid, agrupa e ordena por volume desc.
        var grouped = transactions
            .Where(t => t.Status == TransactionStatus.CAPTURED
                        && !string.IsNullOrEmpty(t.ExternalReferenceId)
                        && t.ExternalReferenceId.StartsWith(prefix, StringComparison.Ordinal))
            .Select(t => new
            {
                Parsed = Guid.TryParse(t.ExternalReferenceId.AsSpan(prefix.Length), out var pid) ? (Guid?)pid : null,
                t.Amount,
            })
            .Where(x => x.Parsed.HasValue)
            .GroupBy(x => x.Parsed!.Value)
            .Select(g => new
            {
                ProductId = g.Key,
                Count = g.Count(),
                Volume = g.Sum(x => x.Amount),
            })
            .OrderByDescending(x => x.Volume)
            .Take(limit)
            .ToList();

        if (grouped.Count == 0) return new List<TopProductDto>();

        // 2) Bulk fetch dos produtos pra evitar N+1 — repo já retorna só os que
        //    existem no tenant (filtra cross-tenant / IDs órfãos silenciosamente).
        var ids = grouped.Select(g => g.ProductId).ToList();
        var products = await productRepository.GetByIdsAsync(tenantId, ids);
        var byId = products.ToDictionary(p => p.Id);

        // 3) Materializa preservando a ordem (volume desc). Produto removido
        //    aparece com placeholder pra não sumir do ranking — o seller ainda
        //    vê que aquela venda aconteceu.
        return grouped.Select(g =>
        {
            byId.TryGetValue(g.ProductId, out var product);
            return new TopProductDto(
                ProductId: g.ProductId,
                Name: product?.Name ?? "(produto removido)",
                Slug: product?.Slug,
                CoverImageUrl: product?.CoverImageUrl,
                Count: g.Count,
                Volume: g.Volume);
        }).ToList();
    }

    public async Task<FinancialHealthDto> GetFinancialHealthAsync(Guid tenantId)
    {
        // 1. Ledger imbalance from latest reconciliation run
        var latestRun = await reconciliationRepository.GetLatestRunAsync(tenantId, "BATCH");
        int ledgerImbalanceCount = latestRun?.IssuesFound ?? 0;
        long? platformDriftCents = latestRun?.PlatformDriftCents;
        string? lastReconciliationStatus = latestRun?.Status;

        // 2. Pending payouts
        var (pendingPayoutsCount, pendingPayoutsTotal) = await payoutRepository.GetPendingSummaryAsync(tenantId);

        // 3. Failed webhook deliveries in last 24 hours
        var since24h = DateTime.UtcNow.AddHours(-24);
        int failedWebhooks = await webhookDeliveryRepository.GetFailedCountSinceAsync(tenantId, since24h);

        // 4. Open disputes
        var (openDisputesCount, disputeExposure) = await disputeRepository.GetOpenDisputeSummaryAsync(tenantId);

        // 5. Split status
        var (splitsPending, splitsFailed) = await splitTransferRepository.GetStatusCountsAsync(tenantId);

        // 6. Reconciliation open issues
        int reconIssuesOpen = await reconciliationRepository.GetOpenIssueCountAsync(tenantId);

        return new FinancialHealthDto(
            LedgerImbalanceCount: ledgerImbalanceCount,
            PlatformDriftCents: platformDriftCents,
            LastReconciliationStatus: lastReconciliationStatus,
            PendingPayoutsCount: pendingPayoutsCount,
            PendingPayoutsTotal: pendingPayoutsTotal,
            FailedWebhooksLast24h: failedWebhooks,
            OpenDisputesCount: openDisputesCount,
            DisputeExposureAmount: disputeExposure,
            SplitsPendingCount: splitsPending,
            SplitsFailedCount: splitsFailed,
            ReconciliationIssuesOpen: reconIssuesOpen
        );
    }
}
