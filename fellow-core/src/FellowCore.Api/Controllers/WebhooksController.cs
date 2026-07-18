using FellowCore.Api.Filters;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("webhooks")]
public class WebhooksController(IWebhooksService webhookService, ILogger<WebhooksController> logger) : ControllerBase
{
    [HttpPost("stripe")]
    [WebhookProvider(PaymentProvider.STRIPE)]
    public async Task<IActionResult> HandleStripe([FromBody] StripeWebhookDto payload)
    {
        logger.LogInformation("Webhook Stripe recebido: {Event}", payload.Type);
        await webhookService.HandleStripeEventAsync(payload);
        return Ok(new { received = true });
    }

    [HttpPost("openpix")]
    [WebhookProvider(PaymentProvider.OPENPIX)]
    public async Task<IActionResult> HandleOpenPix([FromBody] OpenPixWebhookDto payload)
    {
        logger.LogInformation("Webhook OpenPix recebido: {Event}", payload.Event);
        string? authToken = HttpContext.Items["OpenPixAuthToken"] as string;
        await webhookService.HandleOpenPixEventAsync(payload, authToken);
        return Ok(new { received = true });
    }
}
