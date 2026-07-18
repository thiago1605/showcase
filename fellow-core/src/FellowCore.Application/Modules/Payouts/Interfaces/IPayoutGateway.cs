using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Payouts.Interfaces;

/// <summary>
/// Resultado de chamada a um provider de payout. Status devolvido pelo gateway
/// — não é o status do nosso saga, só o que o provider disse sobre o payout
/// específico.
/// </summary>
public record PayoutGatewayResult(string ProviderPayoutId, string ProviderStatus);

/// <summary>
/// Abstração uniforme sobre os providers de payout (Stripe, OpenPix, etc).
/// O <c>WithdrawOrchestrator</c> chama essas operações sem precisar saber
/// detalhes do provider — facilita testar com fakes e adicionar novos providers.
///
/// Cada implementação é responsável por:
///   - Chamar a API do provider com o idempotencyKey fornecido
///   - Mapear o status retornado pra um valor textual estável
///   - Lançar <c>PaymentProviderException</c> em caso de falha
/// </summary>
public interface IPayoutGateway
{
    PaymentProvider Provider { get; }

    /// <summary>Cria payout do balance do seller pra conta bancária dele.</summary>
    Task<PayoutGatewayResult> CreatePayoutAsync(Seller seller, decimal amountInReais, string idempotencyKey, IReadOnlyDictionary<string, string>? metadata = null);

    /// <summary>
    /// Cancela um payout que ainda está em pending no provider. Idempotente —
    /// se já foi pago/cancelado, retorna o status atual sem erro. Usado pelo
    /// caminho de compensação quando outro step do mesmo saga falhou.
    /// Retorna <c>true</c> se cancelamento foi efetivo (status != paid),
    /// <c>false</c> se não pôde cancelar (payout já saiu da pending).
    /// </summary>
    Task<bool> TryCancelPayoutAsync(Seller seller, string providerPayoutId);
}

/// <summary>
/// Factory que resolve qual gateway usar baseado no Provider enum.
/// </summary>
public interface IPayoutGatewayFactory
{
    IPayoutGateway Get(PaymentProvider provider);
}
