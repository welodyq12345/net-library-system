using Library.Reviews.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using ValidationException = FluentValidation.ValidationException;

namespace Library.Reviews.WebApi.Infrastructure;

/// <summary>
/// Глобальний обробник: перетворює доменні та MongoDB-специфічні винятки
/// на <b>ProblemDetails</b> (RFC 9457) з коректним HTTP-статусом.
/// </summary>
/// <remarks>
/// Відповідність винятків статусам (Таблиця 2 методички):
/// <list type="table">
///   <item><term><see cref="NotFoundException"/></term><description>404</description></item>
///   <item><term><see cref="ValidationException"/> (FluentValidation)</term><description>400</description></item>
///   <item><term><see cref="ConflictException"/> (у т. ч. конфлікт версій)</term><description>409</description></item>
///   <item><term><see cref="DomainException"/></term><description>400</description></item>
///   <item><term><c>MongoWriteException</c> з DuplicateKey</term><description>409</description></item>
///   <item><term>інші помилки запису MongoDB</term><description>400</description></item>
///   <item><term><c>MongoConnectionException</c></term><description>503</description></item>
/// </list>
/// Порядок перевірки важливий: похідні типи стоять ПЕРЕД базовим
/// <see cref="DomainException"/>.
/// </remarks>
public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = Map(exception);

        if (status == StatusCodes.Status499ClientClosedRequest)
        {
            logger.LogInformation("Request {Path} was cancelled by the client", http.Request.Path);
            return true;
        }

        if (status >= StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Request {Method} {Path} failed",
                http.Request.Method, http.Request.Path);
        else
            logger.LogWarning("Request {Method} {Path} -> {Status}: {Message}",
                http.Request.Method, http.Request.Path, status, exception.Message);

        http.Response.StatusCode = status;

        // помилки валідації віддаємо з розбивкою по полях
        if (exception is ValidationException validation)
        {
            var errors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

            var vpd = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Type = "https://httpstatuses.io/400",
                Instance = $"{http.Request.Method} {http.Request.Path}"
            };

            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = http, Exception = exception, ProblemDetails = vpd
            });
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= StatusCodes.Status500InternalServerError && !environment.IsDevelopment()
                ? "An unexpected error occurred. See server logs for details."
                : exception.Message,
            Type = $"https://httpstatuses.io/{status}",
            Instance = $"{http.Request.Method} {http.Request.Path}"
        };

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http, Exception = exception, ProblemDetails = problem
        });
    }

    private static (int Status, string Title) Map(Exception exception) => exception switch
    {
        NotFoundException   => (StatusCodes.Status404NotFound,   "Resource not found"),
        ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
        ConflictException   => (StatusCodes.Status409Conflict,   "Conflict"),

        // порушення унікального індексу ux_reviews_book_reader
        MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey }
            => (StatusCodes.Status409Conflict, "Duplicate key"),

        // інші помилки запису — це некоректні дані, а не збій сервера
        MongoWriteException => (StatusCodes.Status400BadRequest, "Write error"),
        MongoBulkWriteException => (StatusCodes.Status400BadRequest, "Bulk write error"),

        // база недоступна — тимчасова проблема інфраструктури
        MongoConnectionException   => (StatusCodes.Status503ServiceUnavailable, "Database unavailable"),
        TimeoutException           => (StatusCodes.Status503ServiceUnavailable, "Database timeout"),

        // базовий доменний виняток — ПІСЛЯ похідних
        DomainException     => (StatusCodes.Status400BadRequest, "Domain rule violated"),

        // ObjectId.Parse на некоректному рядку
        FormatException     => (StatusCodes.Status400BadRequest, "Malformed identifier"),

        OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request cancelled"),

        _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
    };
}
