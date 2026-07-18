using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Auth.DTOs;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Tenant-level user management. JWT-only — never accept API key here, since API keys
/// don't represent a human user and can't authoritatively grant role/seller scope.
///
/// Scoping rules:
/// - Platform operators (JWT without seller_id, role in OWNER/SUPER_ADMIN) see and manage
///   the entire tenant.
/// - Seller-scoped users (JWT with seller_id) see and manage only the user team of their
///   own seller. They cannot list, create, or delete users tied to a different seller.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
[EnableRateLimiting("fixed")]
public class UsersController(IUserService userService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateUserDto dto)
    {
        if (RequireOwnerOrSuperAdmin() is { } block) return block;

        var info = HttpContext.GetAuthInfo()!;
        var tenantId = info.TenantId;

        // Seller-scoped OWNER: nova conta sempre fica vinculada ao próprio seller. Body
        // não pode passar SellerId divergente (403).
        if (info.SellerId is { } scopedSellerId)
        {
            if (dto.SellerId is { } bodySellerId && bodySellerId != scopedSellerId)
                return StatusCode(StatusCodes.Status403Forbidden);

            // Hard rule (Codex 2026-05-06): SUPER_ADMIN é privilégio da plataforma, não
            // do seller. Um seller-OWNER nunca pode criar/elevar alguém para SUPER_ADMIN,
            // mesmo dentro do próprio scope. Permitir OWNER, FINANCE, DEVELOPER, VIEWER,
            // SUPPORT — bloquear SUPER_ADMIN.
            if (dto.Role == UserRole.SUPER_ADMIN)
                return StatusCode(StatusCodes.Status403Forbidden);

            dto = dto with { SellerId = scopedSellerId };
        }

        var result = await userService.CreateAsync(tenantId, dto);
        return Created($"/api/v1/users/{result.Id}", result);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List()
    {
        var info = HttpContext.GetAuthInfo()!;
        var users = await userService.ListAsync(info.TenantId);

        // Seller-scoped: apenas usuários do próprio seller. Platform operator vê tudo.
        if (info.SellerId is { } scopedSellerId)
            users = users.Where(u => u.SellerId == scopedSellerId).ToList();

        return Ok(users);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (RequireOwnerOrSuperAdmin() is { } block) return block;

        var info = HttpContext.GetAuthInfo()!;
        var existing = await userService.GetByIdAsync(info.TenantId, id);
        if (existing is null) return NotFound();
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } scopeBlock) return scopeBlock;

        // Defensive: never let a user delete themselves via this endpoint.
        if (info.UserId == id) return StatusCode(StatusCodes.Status403Forbidden);

        await userService.DeleteAsync(info.TenantId, id);
        return NoContent();
    }

    private IActionResult? RequireOwnerOrSuperAdmin()
    {
        var role = HttpContext.GetAuthInfo()?.Role;
        if (role is UserRole.OWNER or UserRole.SUPER_ADMIN) return null;
        return StatusCode(StatusCodes.Status403Forbidden);
    }
}
