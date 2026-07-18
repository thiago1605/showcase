using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FellowCore.Api.Extensions;
using FellowCore.Domain.Enums;

namespace FellowCore.Api.Auth;

/// <summary>
/// Runs after UseAuthentication. If the request carries a validated JWT principal,
/// extracts tenant_id / seller_id / role / sub claims and populates HttpContext.AuthInfo.
/// Endpoints decorated with [Authorize], [JwtAuth] or [AuthOrApiKeyAuth] will then read
/// the resolved context via the HttpContext extensions.
///
/// The middleware does not authenticate by itself — it only translates a JWT principal
/// into the centralized AuthInfo shape. ApiKeyAuthAttribute populates AuthInfo on its
/// own path. Endpoints with neither [Authorize] nor [ApiKeyAuth] will not have an AuthInfo.
/// </summary>
public class JwtAuthContextMiddleware
{
    private readonly RequestDelegate _next;

    public JwtAuthContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip if AuthInfo was already populated (e.g. ApiKeyAuth ran via global filter)
        // or if there is no authenticated principal.
        if (context.GetAuthInfo() == null
            && context.User?.Identity?.IsAuthenticated == true)
        {
            var principal = context.User;

            // Skip MFA-pending tokens — they are not yet a full session.
            if (principal.FindFirstValue("mfa_pending") != "true")
            {
                var tenantClaim = principal.FindFirstValue("tenant_id");
                if (Guid.TryParse(tenantClaim, out var tenantId))
                {
                    Guid? userId = Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var u)
                        ? u
                        : null;
                    Guid? sellerId = Guid.TryParse(principal.FindFirstValue("seller_id"), out var s)
                        ? s
                        : null;
                    UserRole? role = Enum.TryParse<UserRole>(principal.FindFirstValue("role"), out var r)
                        ? r
                        : null;

                    context.SetAuthInfo(new AuthInfo
                    {
                        TenantId = tenantId,
                        AuthType = AuthType.Jwt,
                        UserId = userId,
                        SellerId = sellerId,
                        Role = role,
                    });
                }
            }
        }

        await _next(context);
    }
}
