using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FellowCore.Api.Auth;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class MasterKeyAuthAttribute : Attribute, IAsyncActionFilter
{
    private const string HeaderName = "X-Master-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var extractedKey)
            || string.IsNullOrWhiteSpace(extractedKey))
        {
            context.Result = new UnauthorizedObjectResult(new { Message = "X-Master-Key header ausente." });
            return;
        }

        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var masterKey = configuration["Security:MasterKey"] ?? "";

        if (string.IsNullOrEmpty(masterKey) || !SecureEquals(extractedKey.ToString(), masterKey))
        {
            context.Result = new UnauthorizedObjectResult(new { Message = "X-Master-Key invalida." });
            return;
        }

        await next();
    }

    private static bool SecureEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
