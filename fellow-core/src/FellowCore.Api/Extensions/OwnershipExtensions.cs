using Microsoft.AspNetCore.Mvc;

namespace FellowCore.Api.Extensions;

/// <summary>
/// Ownership guards for controllers handling per-seller resources. Returns null when
/// the caller is allowed to see/touch the resource, or an IActionResult to short-circuit
/// the action. Use 404 for read endpoints (don't leak existence) and 403 for write
/// endpoints (a seller trying to act on someone else's resource).
///
/// Rules (in order):
///   1. API-key auth: never blocks. The B2B caller is tenant-scoped; the resource was
///      already loaded with tenantId, so authorization is the caller's responsibility.
///   2. JWT without seller_id (platform operator OWNER/FINANCE/SUPER_ADMIN): never blocks.
///   3. JWT with seller_id: ONLY allows when resourceSellerId equals the JWT's seller_id.
///      A null resourceSellerId is treated as "not visible to this seller" — sellers
///      cannot see tenant-wide/orphan resources by default.
///
/// Codex review (2026-05-06): the previous version permitted `resourceSellerId == null`
/// for any caller, which would leak unscoped resources to a seller-OWNER. This guard
/// closes that hole.
/// </summary>
public static class OwnershipExtensions
{
    public static IActionResult? EnforceOwnershipOr404(this ControllerBase controller, Guid? resourceSellerId)
        => Check(controller, resourceSellerId, blocked: () => new NotFoundResult());

    public static IActionResult? EnforceOwnershipOr403(this ControllerBase controller, Guid? resourceSellerId)
        => Check(controller, resourceSellerId, blocked: () => new StatusCodeResult(StatusCodes.Status403Forbidden));

    /// <summary>
    /// Gates an action to platform operators only. Per the "seller-scoped wins over role"
    /// rule, a JWT user that carries seller_id is a seller — not an operator — even if
    /// their role is OWNER. Use on internal/admin endpoints (reconciliation, audit logs,
    /// platform-wide financial-health, provider costs).
    /// </summary>
    public static IActionResult? EnforceOperatorOr403(this ControllerBase controller)
        => controller.HttpContext.IsPlatformOperator()
            ? null
            : new StatusCodeResult(StatusCodes.Status403Forbidden);

    private static IActionResult? Check(ControllerBase controller, Guid? resourceSellerId, Func<IActionResult> blocked)
    {
        var info = controller.HttpContext.GetAuthInfo();
        if (info is null) return blocked();         // No AuthInfo at all: block (defensive — should not happen post-auth filter)
        if (info.IsApiKey) return null;             // B2B: ownership is the caller's responsibility
        if (info.SellerId is null) return null;     // Platform operator: cross-seller allowed
        return resourceSellerId == info.SellerId ? null : blocked();
    }
}
