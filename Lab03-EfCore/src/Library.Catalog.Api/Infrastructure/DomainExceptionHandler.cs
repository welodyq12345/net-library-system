using Library.Catalog.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ValidationException = Library.Catalog.Domain.Exceptions.ValidationException;

namespace Library.Catalog.Api.Infrastructure;

/// <summary>
/// Централізована обробка помилок: усі невиловлені винятки перетворюються на
/// <b>ProblemDetails</b> (RFC 9457) з коректним HTTP-статусом.
/// </summary>
/// <remarks>
/// Правила мапінгу (Частина 4.4 Лаб. 03):
/// <list type="table">
///   <item><term><see cref="EntityNotFoundException"/></term><description>404 Not Found</description></item>
///   <item><term><see cref="ValidationException"/></term><description>400 Bad Request</description></item>
///   <item><term><see cref="ConflictException"/></term><description>409 Conflict</description></item>
///   <item><term><see cref="DomainException"/></term><description>409 Conflict</description></item>
///   <item><term>решта</term><description>500 Internal Server Error</description></item>
/// </list>
/// Порядок перевірки важливий: похідні типи йдуть ПЕРЕД базовим
/// <see cref="DomainException"/>, інакше вони всі потрапили б у 409.
/// Кожен виняток логується рівно ОДИН раз — тут.
/// У production деталі несподіваної помилки клієнту не віддаються.
/// </remarks>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DomainExceptionHandler> _logger;

    public DomainExceptionHandler(
        IProblemDetailsService problemDetails,
        IHostEnvironment environment,
        ILogger<DomainExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _environment = environment;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            EntityNotFoundException    => (StatusCodes.Status404NotFound,   "Resource not found"),
            ValidationException        => (StatusCodes.Status400BadRequest, "Validation failed"),
            ConflictException          => (StatusCodes.Status409Conflict,   "Conflict"),
            DomainException            => (StatusCodes.Status409Conflict,   "Business rule violated"),

            // Страховка на рівні БД: перевірку інваріанта робить сутність, але
            // в умовах конкурентності двоє користувачів можуть пройти її
            // одночасно. Тоді спрацює UNIQUE-індекс — і це теж конфлікт (409),
            // а не внутрішня помилка сервера.
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
                "The record was modified by another user"),
            DbUpdateException due when IsUniqueViolation(due) => (StatusCodes.Status409Conflict,
                "Record with the same unique key already exists"),

            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request cancelled"),
            _                          => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        if (status == StatusCodes.Status499ClientClosedRequest)
        {
            _logger.LogInformation("Request {Path} was cancelled by the client", http.Request.Path);
            return true;
        }

        if (status == StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Request {Method} {Path} failed",
                http.Request.Method, http.Request.Path);
        else
            _logger.LogWarning("Request {Method} {Path} -> {Status}: {Message}",
                http.Request.Method, http.Request.Path, status, exception.Message);

        http.Response.StatusCode = status;

        // помилки валідації віддаємо у форматі ValidationProblemDetails,
        // щоб клієнт отримав розбивку по полях
        if (exception is ValidationException { Errors.Count: > 0 } validation)
        {
            var vpd = new ValidationProblemDetails(
                validation.Errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Type = "https://httpstatuses.io/400",
                Instance = $"{http.Request.Method} {http.Request.Path}"
            };

            return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                Exception = exception,
                ProblemDetails = vpd
            });
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status == StatusCodes.Status500InternalServerError && !_environment.IsDevelopment()
                ? "An unexpected error occurred. See server logs for details."
                : exception.Message,
            Type = $"https://httpstatuses.io/{status}",
            Instance = $"{http.Request.Method} {http.Request.Path}"
        };

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = problem
        });
    }

    /// <summary>
    /// Порушення UNIQUE-індексу (2601) або UNIQUE/PK-обмеження (2627) SQL Server.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
