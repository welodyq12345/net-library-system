using Library.Lending.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Library.Lending.Api.Infrastructure;

/// <summary>
/// Глобальний обробник винятків: перетворює доменні винятки на HTTP-статуси
/// та повертає помилки у стандартному форматі <b>ProblemDetails</b>
/// (RFC 9457, оновлення RFC 7807).
/// </summary>
/// <remarks>
/// Це ЗАВЕРШАЛЬНА ланка ланцюжка «збережувана процедура → DAL → BLL → HTTP»:
/// <list type="table">
///   <item><term><see cref="NotFoundException"/></term><description>404 Not Found (THROW 50001)</description></item>
///   <item><term><see cref="BusinessConflictException"/></term><description>409 Conflict (THROW 50002/50003/50004)</description></item>
///   <item><term><see cref="ValidationException"/></term><description>400 Bad Request</description></item>
///   <item><term>решта</term><description>500 Internal Server Error</description></item>
/// </list>
/// Завдяки цьому обробнику в контролерах НЕМА <c>try/catch</c>: статуси
/// визначаються централізовано в одному місці.
/// Кожен виняток логується РІВНО ОДИН раз — саме тут.
/// </remarks>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<DomainExceptionHandler> _logger;

    public DomainExceptionHandler(
        IProblemDetailsService problemDetails,
        ILogger<DomainExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            NotFoundException         => (StatusCodes.Status404NotFound,   "Resource not found"),
            BusinessConflictException => (StatusCodes.Status409Conflict,   "Business rule violated"),
            ValidationException       => (StatusCodes.Status400BadRequest, "Validation failed"),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request cancelled"),
            _                         => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        // клієнт обірвав запит — відповідати вже нікому
        if (status == StatusCodes.Status499ClientClosedRequest)
        {
            _logger.LogInformation("Request {Path} was cancelled by the client", http.Request.Path);
            return true;
        }

        if (status == StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Request {Method} {Path} failed", http.Request.Method, http.Request.Path);
        else
            _logger.LogWarning("Request {Method} {Path} -> {Status}: {Message}",
                http.Request.Method, http.Request.Path, status, exception.Message);

        http.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title  = title,
            // у production не розкриваємо внутрішні деталі несподіваних помилок
            Detail = status == StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred. See server logs for details."
                : exception.Message,
            Type = $"https://httpstatuses.io/{status}",
            Instance = $"{http.Request.Method} {http.Request.Path}"
        };

        // помилки валідації з BLL віддаємо з розбивкою по полях
        if (exception is ValidationException { Errors.Count: > 0 } validation)
            problem.Extensions["errors"] = validation.Errors;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = problem
        });
    }
}
