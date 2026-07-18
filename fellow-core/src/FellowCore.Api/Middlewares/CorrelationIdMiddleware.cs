using System.Security.Claims;
using Serilog.Context;

namespace FellowCore.Api.Middlewares;

public class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string CorrelationIdHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
                            ?? Guid.NewGuid().ToString("N");

        context.Items["CorrelationId"] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        // Push TenantId and SellerId from JWT claims (if present) so all downstream
        // log entries automatically include tenant context for observability.
        var tenantId = context.User?.FindFirstValue("tenant_id")
                       ?? (context.Items.TryGetValue("TenantId", out var tid) && tid is Guid g ? g.ToString() : null);
        var sellerId = context.User?.FindFirstValue("seller_id");

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("TenantId", tenantId ?? "anonymous"))
        using (LogContext.PushProperty("SellerId", sellerId ?? "none"))
        {
            await next(context);
        }
    }
}
