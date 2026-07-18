using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface IAffiliationRepository
{
    void Add(Affiliation affiliation);
    void Update(Affiliation affiliation);
    Task SaveChangesAsync();

    Task<Affiliation?> GetByIdAsync(Guid tenantId, Guid affiliationId);

    /// <summary>
    /// Lookup por tracking code — entry point pra checkout com ?aff={code}.
    /// Globalmente único, lookup direto sem tenant. Include Product pra
    /// que o caller possa calcular comissão efetiva.
    /// </summary>
    Task<Affiliation?> GetByTrackingCodeAsync(string trackingCode);

    /// <summary>
    /// Afiliação ativa (PENDING ou APPROVED) entre um produto e um seller —
    /// usado para checar duplicatas antes de criar.
    /// </summary>
    Task<Affiliation?> GetActiveByProductAndSellerAsync(Guid productId, Guid affiliateSellerId);

    /// <summary>
    /// Lookup batch do status de afiliação do seller para uma lista de produtos.
    /// Retorna apenas os produtos onde existe afiliação (PENDING/APPROVED/
    /// REJECTED/REVOKED) — produtos sem registro ficam fora do dicionário,
    /// indicando que o seller ainda não interagiu com eles.
    ///
    /// Usado pelo catálogo de afiliação (/marketplace/products) para o card
    /// já refletir o estado correto sem deixar o usuário clicar e receber erro
    /// de duplicata.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AffiliationStatus>> GetStatusByProductIdsAndSellerAsync(
        IReadOnlyList<Guid> productIds,
        Guid affiliateSellerId);

    /// <summary>Lista afiliações pendentes de um produto (painel do produtor).</summary>
    Task<(IReadOnlyList<Affiliation> Items, int TotalCount)> GetByProductAsync(
        Guid tenantId,
        Guid productId,
        AffiliationStatus? status,
        int skip,
        int take);

    /// <summary>Minhas afiliações (painel do afiliado).</summary>
    Task<(IReadOnlyList<Affiliation> Items, int TotalCount)> GetBySellerAsync(
        Guid tenantId,
        Guid affiliateSellerId,
        AffiliationStatus? status,
        int skip,
        int take);

    /// <summary>
    /// Agregação de performance da afiliação: SUM/COUNT em TransactionSplits do
    /// afiliado para o produto, em janela configurável (days) + all-time + ganhos
    /// pendentes + clicks. days valida 7/30/90 no caller; aqui aceita qualquer
    /// int positivo. Join via:
    ///   - TransactionSplit.RecipientId == affiliateSellerId.ToString()
    ///   - Transaction.ExternalReferenceId == "product:{productId}"
    ///   - AffiliateClickEvent.AffiliationId == affiliationId
    /// </summary>
    Task<AffiliateMetricsSnapshot> GetAffiliateMetricsAsync(
        Guid tenantId,
        Guid affiliateSellerId,
        Guid productId,
        Guid affiliationId,
        int days);

    /// <summary>
    /// Registra um click no link de divulgação. Idempotência leve via dedup
    /// por (affiliationId, fingerprint) numa janela de 1h — mesma pessoa
    /// abrindo o link 10x em 30min conta como 1. Retorna true se foi registrado
    /// (= primeiro click da janela), false se foi suprimido como duplicata.
    /// </summary>
    Task<bool> RecordClickAsync(AffiliateClickEvent clickEvent);

    /// <summary>
    /// Top N afiliados do produto ordenados por TPV (vendas brutas atribuídas).
    /// Usado pra leaderboard no painel do produtor — visualizar quem tá performando.
    /// Inclui só afiliados com pelo menos 1 venda. Janela: all-time.
    /// </summary>
    Task<IReadOnlyList<AffiliateLeaderboardEntry>> GetLeaderboardAsync(
        Guid tenantId, Guid productId, int limit);
}

/// <summary>
/// Entry do leaderboard — agregação por afiliado num produto.
/// </summary>
public record AffiliateLeaderboardEntry(
    Guid AffiliationId,
    Guid AffiliateSellerId,
    string? AffiliateName,
    int SalesCount,
    decimal Tpv,
    decimal Earnings
);

/// <summary>
/// Projeção de métricas de afiliado — agregação de TransactionSplits filtrado
/// por recipient + produto. Construído pelo repo a partir de queries
/// agregadas (GROUP BY + SUM/COUNT). A janela é dinâmica (days param).
///
/// SalesInPeriod / ClicksInPeriod: contagem na janela atual de `PeriodDays`.
/// SalesAllTime / ClicksAllTime: agregado total desde a criação da afiliação.
///
/// Timeseries: arrays de `PeriodDays` ints, ordem antigo → novo (índice 0 =
/// (days-1) dias atrás, último = hoje). Usado pra renderizar sparklines.
///
/// PreviousPeriod*: mesma métrica da janela imediatamente anterior (days-2*days
/// atrás). Permite calcular `delta = current/previous - 1` pra badges.
/// </summary>
public record AffiliateMetricsSnapshot(
    int PeriodDays,
    int SalesInPeriod,
    decimal TpvInPeriod,
    decimal EarningsInPeriod,
    int SalesAllTime,
    decimal TpvAllTime,
    decimal EarningsAllTime,
    decimal EarningsPending,
    int ClicksInPeriod,
    int ClicksAllTime,
    // Timeseries — tamanho == PeriodDays
    int[] ClicksByDay,
    int[] SalesByDay,
    // Período anterior (mesma duração imediatamente anterior)
    int PreviousSalesInPeriod,
    decimal PreviousTpvInPeriod,
    decimal PreviousEarningsInPeriod,
    int PreviousClicksInPeriod
);
