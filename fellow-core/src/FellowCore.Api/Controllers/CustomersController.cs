using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Modules.Customers.DTOs;
using FellowCore.Application.Modules.Customers.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/customers")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class CustomersController(ICustomerService customerService) : ControllerBase
{
    [HttpPost]
    [AuditAction("customer.created")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateCustomerDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await customerService.CreateAsync(tenantId, request);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await customerService.GetByIdAsync(tenantId, id);
        return Ok(result);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await customerService.ListAsync(tenantId, page, pageSize);
        return Ok(result);
    }

    [HttpPatch("{id:guid}")]
    [AuditAction("customer.updated")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCustomerDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await customerService.UpdateAsync(tenantId, id, request);
        return Ok(result);
    }

    [HttpPost("{customerId:guid}/payment-methods")]
    [AuditAction("customer.payment_method_added")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddPaymentMethod(Guid customerId, [FromBody] AddPaymentMethodDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await customerService.AddPaymentMethodAsync(tenantId, customerId, request);
        return Created($"/api/v1/customers/{customerId}/payment-methods/{result.Id}", result);
    }
}
