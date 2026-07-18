using System.Security.Cryptography;
using System.Text;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FellowCore.Api.Filters;

public class WebhookAuthFilter(PaymentProvider provider, IConfiguration configuration, ILogger<WebhookAuthFilter> logger) : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        return provider switch
        {
            PaymentProvider.STRIPE => ValidateStripe(context),
            PaymentProvider.OPENPIX => ValidateOpenPix(context),
            _ => Task.CompletedTask
        };
    }

    private async Task ValidateStripe(AuthorizationFilterContext context)
    {
        var signature = context.HttpContext.Request.Headers["Stripe-Signature"].ToString();
        var webhookSecret = configuration["Stripe:WebhookSecret"];

        if (string.IsNullOrEmpty(webhookSecret))
        {
            logger.LogError("Stripe:WebhookSecret nao configurado");
            context.Result = new StatusCodeResult(500);
            return;
        }

        if (string.IsNullOrEmpty(signature))
        {
            logger.LogWarning("Webhook Stripe sem header Stripe-Signature");
            context.Result = new UnauthorizedObjectResult(new { Message = "Stripe-Signature ausente" });
            return;
        }

        // Lê o body raw para validar a assinatura
        context.HttpContext.Request.EnableBuffering();
        context.HttpContext.Request.Body.Position = 0;
        using var reader = new StreamReader(context.HttpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.HttpContext.Request.Body.Position = 0;

        if (!VerifyStripeSignature(body, signature, webhookSecret))
        {
            logger.LogWarning("Assinatura Stripe invalida no webhook");
            context.Result = new UnauthorizedObjectResult(new { Message = "Assinatura Stripe invalida" });
        }
    }

    private Task ValidateOpenPix(AuthorizationFilterContext context)
    {
        var token = context.HttpContext.Request.Headers["Authorization"].ToString();

        if (string.IsNullOrEmpty(token))
        {
            logger.LogWarning("Webhook OpenPix sem header Authorization");
            context.Result = new UnauthorizedObjectResult(new { Message = "Authorization ausente" });
            return Task.CompletedTask;
        }

        // Armazena o token para validação per-seller no handler
        // (o handler verifica se corresponde ao AppId do platform ou do seller da transação)
        context.HttpContext.Items["OpenPixAuthToken"] = token;

        return Task.CompletedTask;
    }

    private const int WebhookToleranceSeconds = 300; // 5 minutes

    private static bool VerifyStripeSignature(string payload, string signatureHeader, string secret)
    {
        // Parse: t=timestamp,v1=signature
        var parts = signatureHeader.Split(',')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

        if (!parts.TryGetValue("t", out var timestamp) || !parts.TryGetValue("v1", out var expectedSignature))
            return false;

        // Replay protection: reject webhooks older than 5 minutes
        if (!long.TryParse(timestamp, out var unixTimestamp))
            return false;

        var webhookTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        var age = DateTimeOffset.UtcNow - webhookTime;
        if (Math.Abs(age.TotalSeconds) > WebhookToleranceSeconds)
            return false;

        var signedPayload = $"{timestamp}.{payload}";
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(signedPayload);

        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        var computedSignature = Convert.ToHexString(hash).ToLower();

        return SecureEquals(computedSignature, expectedSignature);
    }

    private static bool SecureEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
