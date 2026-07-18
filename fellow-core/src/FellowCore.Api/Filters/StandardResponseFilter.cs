using FellowCore.Api.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FellowCore.Api.Filters;

public class StandardResponseFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult objectResult)
        {
            int statusCode = objectResult.StatusCode ?? context.HttpContext.Response.StatusCode;
            bool isSuccess = statusCode is >= 200 and < 300;

            if (objectResult.Value is not ApiResponse)
            {
                if (isSuccess) objectResult.Value = ApiResponse.Ok(objectResult.Value);
                else
                {
                    var errorMessage = ExtractErrorMessage(objectResult.Value);
                    objectResult.Value = ApiResponse.Fail(errorMessage);
                }
            }
        }


        await next();
    }

    private static string ExtractErrorMessage(object? value)
    {
        // ValidationProblemDetails (gerado por [ApiController] + ModelState/FluentValidation)
        // não tem `Message` — é um dicionário { campo → string[] }. Sem este caso o filter
        // chamava `.ToString()` que devolve o nome do tipo, mostrando ao cliente
        // "Microsoft.AspNetCore.Mvc.ValidationProblemDetails" em vez das mensagens reais.
        if (value is ValidationProblemDetails vpd)
        {
            var messages = vpd.Errors
                .SelectMany(kvp => kvp.Value.Select(msg => string.IsNullOrEmpty(kvp.Key) ? msg : $"{kvp.Key}: {msg}"))
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            if (messages.Count > 0) return string.Join(" | ", messages);
            return vpd.Title ?? vpd.Detail ?? "Dados inválidos.";
        }

        if (value is ProblemDetails pd)
            return pd.Detail ?? pd.Title ?? "Erro na requisição.";

        if (value != null && value.GetType().GetProperty("Message") is { } prop)
            return prop.GetValue(value)?.ToString() ?? "Erro na requisição.";

        return value?.ToString() ?? "Ocorreu um erro inesperado.";
    }
}