using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Setup;

public class OpenPixWebhookSetup(
    IOpenPixApiClient openPixApi,
    IConfiguration configuration,
    ILogger<OpenPixWebhookSetup> logger) : BackgroundService
{
    // Woovi exige UM webhook por evento. TRANSACTION_RECEIVED é o evento "dinheiro caiu"
    // (default e mais confiável); CHARGE_COMPLETED é redundante mas mantido pra robustez.
    // CHARGE_EXPIRED sinaliza DECLINED. REFUND_RECEIVED sinaliza REFUNDED.
    // ACCOUNT_REGISTER_* roteia decisões de KYC pra subcontas.
    private static readonly string[] WebhookEvents =
    [
        "OPENPIX:TRANSACTION_RECEIVED",
        "OPENPIX:CHARGE_COMPLETED",
        "OPENPIX:CHARGE_EXPIRED",
        "OPENPIX:TRANSACTION_REFUND_RECEIVED",
        "ACCOUNT_REGISTER_APPROVED",
        "ACCOUNT_REGISTER_REJECTED",
        "ACCOUNT_REGISTER_PENDING"
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? appId = configuration["OpenPix:AppId"];
        string? webhookUrl = configuration["OpenPix:WebhookUrl"];

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(webhookUrl))
        {
            logger.LogWarning("OpenPix:AppId ou OpenPix:WebhookUrl nao configurados. Registro de webhook ignorado.");
            return;
        }

        // Um POST por evento (schema Woovi exige `event` singular). Se algum já existir
        // (409/conflict), a Woovi devolve erro — logamos e seguimos pros próximos.
        int registered = 0;
        foreach (var ev in WebhookEvents)
        {
            try
            {
                await openPixApi.RegisterWebhookAsync(appId, webhookUrl, $"FellowCore Webhook - {ev}", ev);
                registered++;
                logger.LogInformation("Webhook OpenPix registrado: {Event} -> {Url}", ev, webhookUrl);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao registrar webhook OpenPix {Event} (pode já existir; verifique manualmente)", ev);
            }
        }
        logger.LogInformation("Registro de webhooks OpenPix finalizado: {Registered}/{Total}", registered, WebhookEvents.Length);
    }
}
