using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using FellowCore.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Payouts.Services;

public class OpenPixPayoutProcessor(
    IOpenPixApiClient openPixApi,
    IConfiguration configuration,
    ILogger<OpenPixPayoutProcessor> logger) : IPayoutProcessor
{
    /// <summary>
    /// Taxa cobrada pela OpenPix em saques abaixo de R$500.
    /// </summary>
    public const decimal OpenPixWithdrawFeeThreshold = 500m;
    public const decimal OpenPixWithdrawFee = 1m;

    public async Task<PayoutResult> ProcessAsync(Payout payout, Seller seller)
    {
        string appId = configuration["OpenPix:AppId"]
            ?? throw new InvalidOperationException("OpenPix:AppId nao configurado.");

        if (string.IsNullOrWhiteSpace(seller.ExternalAccountId))
            return new PayoutResult(false, FailureReason: "Seller nao possui conta BaaS (ExternalAccountId) cadastrada.");

        int valueInCents = (int)(payout.Amount * 100);

        logger.LogInformation(
            "Sacando da conta BaaS OpenPix para payout {PayoutId} | Seller: {SellerId} | AccountId: {AccountId} | Valor: {Value} centavos",
            payout.Id, seller.Id, seller.ExternalAccountId, valueInCents);

        var withdrawRequest = new OpenPixWithdrawRequest(Value: valueInCents);
        var response = await openPixApi.WithdrawFromAccountAsync(appId, seller.ExternalAccountId, withdrawRequest);

        var withdraw = response.Withdraw;
        var transaction = withdraw?.Transaction;

        if (transaction == null)
            return new PayoutResult(false, FailureReason: "OpenPix retornou resposta sem dados de transacao.");

        string transactionId = transaction.EndToEndId ?? $"withdraw-{payout.Id:N}";

        logger.LogInformation(
            "Payout {PayoutId} saque realizado. EndToEndId: {EndToEndId} | Valor: {Value}",
            payout.Id, transaction.EndToEndId, transaction.Value);

        return new PayoutResult(true, TransactionId: transactionId);
    }

    /// <summary>
    /// Calcula a fee total de um payout: fee fixa FellowCore + fee OpenPix (R$1 se < R$500).
    /// </summary>
    public static decimal CalculateTotalFee(decimal amount, decimal fellowPayFixedFee)
    {
        decimal openpixFee = amount < OpenPixWithdrawFeeThreshold ? OpenPixWithdrawFee : 0m;
        return fellowPayFixedFee + openpixFee;
    }
}
