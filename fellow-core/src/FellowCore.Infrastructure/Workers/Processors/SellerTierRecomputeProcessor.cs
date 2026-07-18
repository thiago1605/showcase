using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Sellers.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface ISellerTierRecomputeProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Job mensal que recalcula o <see cref="SellerTierProfile"/> de cada seller ativo.
///
/// Estratégia de tier:
///   1. Lê TPV90d (janela rolling de 90 dias) — sinal estável, menos suscetível a
///      sazonalidade do que TPV30d. <see cref="SellerTierService.Resolve"/> deriva
///      o tier alvo a partir desse TPV.
///   2. Checa <b>consistência</b> pra subir: TPV30d (último mês) também precisa
///      cruzar o floor do novo tier. Evita "upgrade por uma única mega-Black Friday
///      que cabe na janela 90d".
///   3. Checa <b>cooldown de descida</b>: se UpgradedAt &lt; 60 dias atrás, mantém
///      o tier atual (dá fôlego pro seller — não rebaixa logo após promover).
///   4. Checa <b>freeze anti-chargeback</b>: se chargebackRate90d &gt; 2% (lido do
///      <see cref="SellerRiskProfile"/>), congela subida por 60 dias. Descida ainda
///      acontece (o entity enforça isso em <see cref="SellerTierProfile.Apply"/>).
///   5. Aplica o resultado via <c>Apply</c> e persiste. Eventos publicados
///      pra transições reais (Upgraded/Downgraded), não para Unchanged/BlockedByFreeze.
///
/// Idempotente: re-rodar no mesmo dia produz o mesmo resultado (atualiza
/// Tpv90dSnapshotBrl + ComputedAt mas não dispara evento se tier não mudou).
///
/// Sprint 1 #2 (este commit): job + cooldown + freeze + evento.
/// Sprint 1 #3: PricingService consome <c>SellerTierProfile.Tier</c> em runtime.
/// </summary>
public class SellerTierRecomputeProcessor(
    ISellerRepository sellerRepository,
    ISellerTierProfileRepository tierProfileRepository,
    ISellerRiskProfileRepository riskProfileRepository,
    IDomainEventDispatcher eventDispatcher,
    TimeProvider timeProvider,
    ILogger<SellerTierRecomputeProcessor> logger) : ISellerTierRecomputeProcessor
{
    /// <summary>Sellers com chargeback &gt; este threshold sobem cooldown de freeze.</summary>
    private const decimal ChargebackFreezeThreshold = 0.02m; // 2%

    /// <summary>Duração do freeze quando chargeback estoura.</summary>
    private static readonly TimeSpan FreezeDuration = TimeSpan.FromDays(60);

    /// <summary>Mínimo entre upgrades sucessivos pra evitar oscilação.</summary>
    private static readonly TimeSpan DowngradeCooldown = TimeSpan.FromDays(60);

    /// <summary>Janela TPV pra avaliação principal de tier.</summary>
    private static readonly TimeSpan Tpv90dWindow = TimeSpan.FromDays(90);

    /// <summary>Janela TPV pra checagem de consistência (impede upgrade por spike único).</summary>
    private static readonly TimeSpan Tpv30dWindow = TimeSpan.FromDays(30);

    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var since90d = now - Tpv90dWindow;
        var since30d = now - Tpv30dWindow;

        logger.LogInformation("[TIER_RECOMPUTE] Iniciando — now={Now}", now);

        var sellers = await sellerRepository.GetActiveTenantSellerPairsAsync();
        if (sellers.Count == 0)
        {
            logger.LogInformation("[TIER_RECOMPUTE] Nenhum seller ativo");
            return;
        }

        int upgraded = 0, downgraded = 0, unchanged = 0, blocked = 0, created = 0, frozen = 0;

        foreach (var (tenantId, sellerId) in sellers)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var tpv90d = await sellerRepository.GetCapturedNetSumAsync(tenantId, sellerId, since90d);
                var tpv30d = await sellerRepository.GetCapturedNetSumAsync(tenantId, sellerId, since30d);

                var (targetTier, _, _) = SellerTierService.Resolve(tpv90d);

                var profile = await tierProfileRepository.GetBySellerIdAsync(tenantId, sellerId);

                // 1. Cria profile pra seller novo (primeiro recálculo). Sem cooldown,
                //    sem checagem de consistência — é a primeira atribuição.
                if (profile == null)
                {
                    profile = SellerTierProfile.Create(tenantId, sellerId, targetTier, tpv90d, now);
                    tierProfileRepository.Add(profile);
                    created++;
                    logger.LogInformation("[TIER_RECOMPUTE] Profile criado seller={SellerId} tier={Tier} tpv90d={Tpv}",
                        sellerId, targetTier, tpv90d);
                    continue;
                }

                // 2. Freeze anti-chargeback ANTES do Apply pra que o entity respeite o IsFrozen.
                //    Recalcula o freeze a cada rodada — assim sai automaticamente quando o
                //    chargeback cair abaixo do threshold (na próxima execução o reason muda
                //    ou Unfreeze é chamado).
                await EnforceChargebackFreezeAsync(profile, sellerId, now);

                // 3. Cooldown de descida: se foi promovido há menos de 60d, não rebaixa.
                //    Apenas atualiza o snapshot pra refletir TPV atual.
                bool isDowngrade = (int)targetTier < (int)profile.Tier;
                if (isDowngrade && profile.UpgradedAt.HasValue && (now - profile.UpgradedAt.Value) < DowngradeCooldown)
                {
                    logger.LogInformation("[TIER_RECOMPUTE] Cooldown de descida seller={SellerId} mantém={Tier} (upgrade {Days}d atrás)",
                        sellerId, profile.Tier, (now - profile.UpgradedAt.Value).TotalDays);
                    profile.Apply(profile.Tier, tpv90d, now); // só atualiza snapshot
                    tierProfileRepository.Update(profile);
                    unchanged++;
                    continue;
                }

                // 4. Consistência de subida: TPV30d (último mês) tem que cruzar o floor
                //    do novo tier. Caso contrário, mantém. Evita upgrade por spike.
                bool isUpgrade = (int)targetTier > (int)profile.Tier;
                if (isUpgrade)
                {
                    var (tierFromMonthly, _, _) = SellerTierService.Resolve(tpv30d);
                    if ((int)tierFromMonthly < (int)targetTier)
                    {
                        logger.LogInformation("[TIER_RECOMPUTE] Consistência negada seller={SellerId} tpv90d→{Target} mas tpv30d→{Monthly}",
                            sellerId, targetTier, tierFromMonthly);
                        profile.Apply(profile.Tier, tpv90d, now); // só snapshot
                        tierProfileRepository.Update(profile);
                        unchanged++;
                        continue;
                    }
                }

                // 5. Aplica.
                var previousTier = profile.Tier;
                var transition = profile.Apply(targetTier, tpv90d, now);
                tierProfileRepository.Update(profile);

                switch (transition)
                {
                    case SellerTierTransition.Upgraded:
                        upgraded++;
                        await PublishAsync(sellerId, tenantId, previousTier, profile.Tier, transition, tpv90d, ct);
                        logger.LogInformation("[TIER_RECOMPUTE] UPGRADE seller={SellerId} {Prev}→{New} tpv90d={Tpv}",
                            sellerId, previousTier, profile.Tier, tpv90d);
                        break;
                    case SellerTierTransition.Downgraded:
                        downgraded++;
                        await PublishAsync(sellerId, tenantId, previousTier, profile.Tier, transition, tpv90d, ct);
                        logger.LogWarning("[TIER_RECOMPUTE] DOWNGRADE seller={SellerId} {Prev}→{New} tpv90d={Tpv}",
                            sellerId, previousTier, profile.Tier, tpv90d);
                        break;
                    case SellerTierTransition.BlockedByFreeze:
                        blocked++;
                        logger.LogInformation("[TIER_RECOMPUTE] BLOCKED_BY_FREEZE seller={SellerId} would-be={Target} stays={Tier}",
                            sellerId, targetTier, profile.Tier);
                        break;
                    case SellerTierTransition.Unchanged:
                        unchanged++;
                        break;
                }

                if (profile.IsFrozen(now)) frozen++;
            }
            catch (Exception ex)
            {
                // Não interrompe o batch — log e segue. Próximo run pega.
                logger.LogError(ex, "[TIER_RECOMPUTE] Erro processando seller={SellerId}", sellerId);
            }
        }

        await tierProfileRepository.SaveChangesAsync();

        logger.LogInformation(
            "[TIER_RECOMPUTE] Concluído. created={Created} upgraded={Up} downgraded={Down} blocked={Blocked} unchanged={Unchanged} frozen={Frozen} total={Total}",
            created, upgraded, downgraded, blocked, unchanged, frozen, sellers.Count);
    }

    private async Task EnforceChargebackFreezeAsync(SellerTierProfile profile, Guid sellerId, DateTime now)
    {
        var riskProfile = await riskProfileRepository.GetBySellerIdAsync(sellerId);

        // Sem dados de risco — sem condição pra congelar. Provavelmente seller novo
        // (SellerRiskProfileRefreshProcessor ainda não rodou pra ele). Conservador:
        // não congela, mas tampouco desbloqueia se já estiver congelado.
        if (riskProfile == null) return;

        bool shouldFreeze = riskProfile.ChargebackRate > ChargebackFreezeThreshold;

        if (shouldFreeze)
        {
            // Estende ou cria freeze. Reason inclui o rate observado pra audit trail.
            var until = now + FreezeDuration;
            var reason = $"chargeback_rate={riskProfile.ChargebackRate:P2} > {ChargebackFreezeThreshold:P0} (sample={riskProfile.CapturedCount90d})";
            profile.Freeze(until, reason, now);
        }
        else if (profile.IsFrozen(now))
        {
            // Chargeback caiu abaixo do threshold E o freeze atual era anti-chargeback
            // (FreezeReason começa com "chargeback_rate"). Não toca freeze que veio de
            // admin override (Sprint 2+).
            if (profile.FreezeReason?.StartsWith("chargeback_rate=", StringComparison.Ordinal) == true)
            {
                profile.Unfreeze(now);
            }
        }
    }

    private Task PublishAsync(
        Guid sellerId,
        Guid tenantId,
        SellerTier previousTier,
        SellerTier newTier,
        SellerTierTransition transition,
        decimal tpv90d,
        CancellationToken ct)
    {
        var evt = new SellerTierChangedEvent(sellerId, tenantId, previousTier, newTier, transition, tpv90d);
        return eventDispatcher.DispatchAsync(new IDomainEvent[] { evt }, ct);
    }
}
