using FellowCore.Application.Modules.AuditLogs.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace FellowCore.Api.Filters;

public class AuditActionFilter(IAuditLogService auditLogService, ILogger<AuditActionFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();

        try
        {
            var method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
                return;

            if (executed.Exception != null && !executed.ExceptionHandled)
                return;

            var actionAttribute = context.ActionDescriptor.EndpointMetadata
                .OfType<AuditActionAttribute>()
                .FirstOrDefault();

            if (actionAttribute == null)
                return;

            if (!context.HttpContext.Items.TryGetValue("TenantId", out var tenantIdObj) || tenantIdObj is not Guid tenantId)
                return;

            var statusCode = executed.Result switch
            {
                NoContentResult => 204,
                CreatedResult => 201,
                ObjectResult obj => obj.StatusCode ?? 200,
                StatusCodeResult sc => sc.StatusCode,
                _ => 200
            };

            var resourceId = context.RouteData.Values.TryGetValue("id", out var idVal)
                ? idVal?.ToString()
                : null;

            // Also check customerId route param (e.g. payment-methods)
            if (resourceId == null && context.RouteData.Values.TryGetValue("customerId", out var custId))
                resourceId = custId?.ToString();

            var correlationId = context.HttpContext.Items.TryGetValue("CorrelationId", out var corrObj)
                ? corrObj?.ToString()
                : null;

            var ipAddress = context.HttpContext.Connection.RemoteIpAddress?.ToString();

            await auditLogService.LogAsync(tenantId, actionAttribute.Action, resourceId, ipAddress, correlationId, statusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao registrar audit log para {Action}", context.ActionDescriptor.DisplayName);
        }
    }
}
