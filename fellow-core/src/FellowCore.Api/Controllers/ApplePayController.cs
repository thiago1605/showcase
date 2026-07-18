using FellowCore.Api.Auth;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/apple-pay")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class ApplePayController(
    IStripeApiClient stripeApi,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>
    /// Register a domain for Apple Pay. Required before Apple Pay works on that domain.
    /// </summary>
    [HttpPost("domains")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDomain([FromBody] RegisterApplePayDomainRequest request)
    {
        string apiKey = GetStripeKey();
        var result = await stripeApi.RegisterApplePayDomainAsync(apiKey, request.DomainName);
        return Created("", new { result.Id, result.DomainName, result.Livemode });
    }

    /// <summary>
    /// List all registered Apple Pay domains.
    /// </summary>
    [HttpGet("domains")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDomains()
    {
        string apiKey = GetStripeKey();
        var domains = await stripeApi.ListApplePayDomainsAsync(apiKey);
        return Ok(domains.Select(d => new { d.Id, d.DomainName, d.Livemode }));
    }

    /// <summary>
    /// Remove a registered Apple Pay domain.
    /// </summary>
    [HttpDelete("domains/{domainId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteDomain(string domainId)
    {
        string apiKey = GetStripeKey();
        await stripeApi.DeleteApplePayDomainAsync(apiKey, domainId);
        return NoContent();
    }

    private string GetStripeKey() =>
        configuration["Stripe:SecretKey"]
            ?? throw new InvalidOperationException("Stripe:SecretKey nao configurado.");
}

public record RegisterApplePayDomainRequest(string DomainName);
