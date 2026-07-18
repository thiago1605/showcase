using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using NSubstitute;

namespace FellowCore.Application.Tests.Helpers;

/// <summary>
/// Helper compartilhado pra mockar <see cref="IInboundWebhookEventRepository"/>
/// nos testes que exercitam <c>HandleStripeEventAsync</c> / <c>HandleOpenPixEventAsync</c>.
///
/// Default: <c>CreatePermissive()</c> — guard sempre deixa passar (cria entity nova
/// pra cada chamada). Útil pra testes que não estão verificando o dedup em si,
/// mas precisam que o flow real do handler execute.
///
/// Pra testar dedup explícito: instancia o mock manualmente e configura
/// <c>TryRegisterReceivedAsync(...).Returns((InboundWebhookEvent?)null)</c>.
/// </summary>
internal static class InboundWebhookGuardMockHelper
{
    /// <summary>
    /// Mock que SEMPRE retorna entity nova (= "deixa passar, primeira vez").
    /// Compatível com testes que verificam idempotência via guards downstream
    /// (status da TX, balance do ledger, etc) em vez do guard de dedup do webhook.
    /// </summary>
    public static IInboundWebhookEventRepository CreatePermissive()
    {
        var mock = Substitute.For<IInboundWebhookEventRepository>();
        mock.TryRegisterReceivedAsync(
                Arg.Any<PaymentProvider>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => InboundWebhookEvent.CreateReceived(
                (PaymentProvider)call[0], (string)call[1], (string)call[2]));
        return mock;
    }
}
