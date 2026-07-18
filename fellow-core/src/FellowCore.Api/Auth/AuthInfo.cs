using FellowCore.Domain.Enums;

namespace FellowCore.Api.Auth;

public enum AuthType
{
    ApiKey,
    Jwt
}

/// <summary>
/// Resolved authentication context for the current request.
/// Populated by the auth pipeline (JwtAuthContextMiddleware or ApiKeyAuthAttribute)
/// and stored in HttpContext.Items["AuthInfo"]. Read via HttpContextExtensions.
/// </summary>
public sealed class AuthInfo
{
    public required Guid TenantId { get; init; }
    public AuthType AuthType { get; init; }

    // Populated only when AuthType == Jwt
    public Guid? UserId { get; init; }
    public Guid? SellerId { get; init; }
    public UserRole? Role { get; init; }

    public bool IsApiKey => AuthType == AuthType.ApiKey;
    public bool IsJwt => AuthType == AuthType.Jwt;

    /// <summary>
    /// True only for accounts that may legitimately query data outside any seller scope
    /// (cross-seller reconciliation, financial-health, audit logs, etc.).
    ///
    /// Hard rule (seller-scoped wins over role): a JWT that carries seller_id is always
    /// seller-scoped, even if the role is OWNER. This prevents a seller-OWNER from
    /// escalating to cross-seller reads by passing ?sellerId=… in queries. To act as a
    /// platform operator, the user must have SellerId == null AND a privileged role.
    /// </summary>
    public bool IsPlatformOperator =>
        AuthType == AuthType.Jwt
        && SellerId is null
        && Role is UserRole.SUPER_ADMIN or UserRole.OWNER or UserRole.FINANCE;
}
