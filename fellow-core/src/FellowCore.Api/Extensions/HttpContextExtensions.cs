using FellowCore.Api.Auth;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FellowCore.Api.Extensions;

public static class HttpContextExtensions
{
    private const string AuthInfoKey = "AuthInfo";

    /// <summary>
    /// Stores the resolved AuthInfo for the current request. Called by the auth
    /// pipeline (ApiKeyAuthAttribute or the JWT context middleware).
    /// </summary>
    public static void SetAuthInfo(this HttpContext context, AuthInfo authInfo)
    {
        context.Items[AuthInfoKey] = authInfo;
        // Back-compat: existing code reads HttpContext.Items["TenantId"] directly.
        context.Items["TenantId"] = authInfo.TenantId;
    }

    public static AuthInfo? GetAuthInfo(this HttpContext context)
    {
        return context.Items.TryGetValue(AuthInfoKey, out var v) ? v as AuthInfo : null;
    }

    public static Guid GetTenantId(this HttpContext context)
    {
        var info = context.GetAuthInfo();
        if (info != null) return info.TenantId;

        // Back-compat fallback for any legacy code path that still pokes Items["TenantId"]
        // directly without going through the new pipeline.
        if (context.Items.TryGetValue("TenantId", out var tenantIdObj) && tenantIdObj is Guid tenantId)
            return tenantId;

        throw new UnauthorizedAccessException(
            "Tenant não identificado. A rota precisa de [ApiKeyAuth], [JwtAuth] ou [AuthOrApiKeyAuth].");
    }

    public static Guid? GetCurrentUserId(this HttpContext context) => context.GetAuthInfo()?.UserId;

    public static Guid? GetCurrentSellerId(this HttpContext context) => context.GetAuthInfo()?.SellerId;

    public static UserRole? GetCurrentRole(this HttpContext context) => context.GetAuthInfo()?.Role;

    public static bool IsApiKeyAuth(this HttpContext context) => context.GetAuthInfo()?.IsApiKey == true;

    public static bool IsJwtAuth(this HttpContext context) => context.GetAuthInfo()?.IsJwt == true;

    public static bool IsPlatformOperator(this HttpContext context)
        => context.GetAuthInfo()?.IsPlatformOperator == true;

    /// <summary>
    /// For seller-scoped portal endpoints: returns the SellerId the request must be
    /// scoped to. Resolution rules (in order):
    ///
    /// 1. API key auth → returns requestedSellerId as-is (B2B caller picks scope).
    /// 2. JWT with seller_id (seller-scoped account, even if role is OWNER) → ALWAYS
    ///    returns AuthInfo.SellerId. The requested sellerId from query is silently
    ///    overridden so a seller-OWNER cannot read another seller's data via ?sellerId=.
    /// 3. JWT platform operator (no seller_id, role in SUPER_ADMIN/OWNER/FINANCE) →
    ///    returns requestedSellerId, or null for "all sellers in tenant".
    /// 4. JWT non-privileged without seller_id → returns null (caller should reject).
    /// </summary>
    public static Guid? ResolveSellerScope(this HttpContext context, Guid? requestedSellerId)
    {
        var info = context.GetAuthInfo();
        if (info == null) return requestedSellerId;
        if (info.IsApiKey) return requestedSellerId;

        // Hard rule: seller-scoped wins over role. A user with seller_id can only
        // see their own seller, period.
        if (info.SellerId is { } sellerId) return sellerId;

        if (info.IsPlatformOperator) return requestedSellerId;

        return null;
    }

    /// <summary>
    /// Versão segura de <see cref="ResolveSellerScope"/> que NUNCA retorna
    /// "all sellers" implicitamente para users JWT não-privilegiados.
    /// Use em endpoints seller-scoped (dashboard, transações por seller, etc).
    ///
    /// Retorna um <see cref="ObjectResult"/> não-nulo quando deve negar:
    /// 401 se não autenticado, 403 se autenticado mas sem escopo definível
    /// (caller é JWT sem seller_id e não é platform operator — caso típico
    /// de novo user criado via SSO antes de ser vinculado a um seller).
    ///
    /// O caller deve fazer:
    /// <code>
    /// var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
    /// if (deny is not null) return deny;
    /// </code>
    /// </summary>
    public static (ObjectResult? denyResponse, Guid? sellerId) RequireSellerScope(
        this HttpContext context,
        Guid? requestedSellerId)
    {
        var info = context.GetAuthInfo();
        if (info == null)
        {
            return (
                new ObjectResult(new
                {
                    error = "Auth.Unauthenticated",
                    message = "Sessão inválida.",
                })
                { StatusCode = StatusCodes.Status401Unauthorized },
                null);
        }

        // API key: scope vem do query param (caller B2B sabe o que tá pedindo)
        if (info.IsApiKey) return (null, requestedSellerId);

        // JWT seller-scoped: força o próprio (mesmo se o caller mandou ?sellerId=
        // de outro seller — proteção contra horizontal privilege escalation)
        if (info.SellerId is { } sellerId) return (null, sellerId);

        // JWT platform operator (SUPER_ADMIN/OWNER/FINANCE sem seller próprio):
        // pode filtrar por seller ou pedir agregado da tenant inteira (null)
        if (info.IsPlatformOperator) return (null, requestedSellerId);

        // JWT não-privilegiado sem seller_id — retornar dados da tenant aqui
        // vazaria informação de outros sellers. NEGA com mensagem orientada.
        return (
            new ObjectResult(new
            {
                error = "Auth.NoSellerScope",
                message = "Sua conta ainda não está vinculada a um produtor. " +
                    "Entre em contato com o administrador para concluir o cadastro.",
            })
            { StatusCode = StatusCodes.Status403Forbidden },
            null);
    }
}
