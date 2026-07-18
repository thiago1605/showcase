using FellowCore.Application.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Api.ExceptionHandlers;

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            UnauthorizedException   => (StatusCodes.Status401Unauthorized,         "Não autorizado"),
            NotFoundException       => (StatusCodes.Status404NotFound,            "Recurso não encontrado"),
            ConflictException       => (StatusCodes.Status409Conflict,             "Conflito"),
            ValidationException     => (StatusCodes.Status422UnprocessableEntity,  "Dados inválidos"),
            BusinessException       => (StatusCodes.Status400BadRequest,           "Regra de negócio violada"),
            ConfigurationException  => (StatusCodes.Status500InternalServerError,  "Erro de configuração"),
            PaymentProviderException=> (StatusCodes.Status502BadGateway,           "Erro no provedor de pagamento"),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,        "Conflito de concorrência"),
            NotSupportedException   => (StatusCodes.Status400BadRequest,           "Operação não suportada"),
            _                       => (StatusCodes.Status500InternalServerError,  "Erro interno no servidor")
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Erro não tratado na API");
        else
            logger.LogWarning(exception, "Erro de negócio: {Type}", exception.GetType().Name);

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status == StatusCodes.Status500InternalServerError
                ? "Ocorreu uma falha inesperada. Nossa equipe já foi notificada."
                : exception.Message
        };

        if (exception is AppException appEx)
            problemDetails.Extensions["errorCode"] = appEx.Error.Code;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
