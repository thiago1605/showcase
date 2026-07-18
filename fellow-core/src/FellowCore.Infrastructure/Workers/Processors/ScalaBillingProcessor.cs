using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public class ScalaBillingProcessor(
    ISellerRepository sellerRepository,
    ILedgerService ledgerService,
    ILogger<ScalaBillingProcessor> logger) : IScalaBillingProcessor
{
    private const string PlanCode = "SCALA";
    private const decimal MonthlyFee = 499m;

    public async Task ProcessMonthlyBillingAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var year = now.Year;
        var month = now.Month;

        var sellers = await sellerRepository.GetActiveSellersByPlanCodeAsync(PlanCode);

        if (sellers.Count == 0)
        {
            logger.LogInformation("[SCALA_BILLING] Nenhum seller ativo com plano SCALA encontrado para {Year}/{Month:D2}", year, month);
            return;
        }

        logger.LogInformation("[SCALA_BILLING] Processando cobranca mensal para {Count} seller(s) no plano SCALA — {Year}/{Month:D2}",
            sellers.Count, year, month);

        foreach (var seller in sellers)
        {
            if (ct.IsCancellationRequested) break;

            var ledgerReference = $"SCALA_BILLING_{seller.Id}_{year}_{month}";

            try
            {
                await ledgerService.DebitSellerAsync(
                    seller.TenantId,
                    seller.Id,
                    MonthlyFee,
                    $"Mensalidade plano SCALA — {year}/{month:D2}",
                    ledgerReference);

                logger.LogInformation(
                    "[SCALA_BILLING] Cobranca de R${Amount} realizada com sucesso para seller {SellerId} — referencia {Reference}",
                    MonthlyFee, seller.Id, ledgerReference);
            }
            catch (BusinessException ex) when (ex.Message.Contains("Saldo insuficiente"))
            {
                logger.LogWarning(
                    "[SCALA_BILLING] Saldo insuficiente para seller {SellerId} (tenant {TenantId}). " +
                    "Marcado para revisao manual. Referencia: {Reference}",
                    seller.Id, seller.TenantId, ledgerReference);
            }
            catch (NotFoundException ex)
            {
                logger.LogWarning(ex,
                    "[SCALA_BILLING] Conta ledger nao encontrada para seller {SellerId} (tenant {TenantId}). " +
                    "Marcado para revisao manual. Referencia: {Reference}",
                    seller.Id, seller.TenantId, ledgerReference);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[SCALA_BILLING] Erro inesperado ao processar cobranca para seller {SellerId} (tenant {TenantId}). " +
                    "Referencia: {Reference}",
                    seller.Id, seller.TenantId, ledgerReference);
            }
        }
    }
}
