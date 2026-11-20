using System.Diagnostics;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Library.Reviews.Application.Common.Behaviors;

/* =============================================================================
   Pipeline Behaviors — «обгортки» навколо виклику обробника. Кожна команда
   й кожен запит проходить крізь них, тому валідація, логування та вимірювання
   часу написані ОДИН раз, а не в кожному обробнику.

   Той самий принцип, що й middleware ASP.NET Core, але на рівні MediatR.
   Порядок реєстрації визначає порядок виконання:
       Logging (зовнішній) -> Validation -> Performance (біля обробника)
   ============================================================================= */

/// <summary>
/// Виконує всі валідатори FluentValidation для запиту ДО виклику обробника.
/// </summary>
/// <remarks>
/// Помилки збираються разом і кидаються одним <see cref="ValidationException"/> —
/// обробник при цьому <b>не викликається</b> взагалі. Аналог
/// <c>ValidatorBehavior</c> в eShop.
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators,
    ILogger<ValidationBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var applicable = validators.ToList();
        if (applicable.Count == 0)
            return await next(ct);

        // ВАЖЛИВО: кожен валідатор отримує ВЛАСНИЙ ValidationContext.
        // Якщо передати один спільний контекст, FluentValidation накопичує
        // в ньому помилки, і результат другого валідатора міститиме ще й
        // помилки першого — на виході вони подвоюються.
        var results = await Task.WhenAll(applicable.Select(
            v => v.ValidateAsync(new ValidationContext<TRequest>(request), ct)));

        var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToList();

        if (failures.Count > 0)
        {
            logger.LogWarning(
                "Validation failed for {RequestName}: {Errors}",
                typeof(TRequest).Name,
                string.Join("; ", failures.Select(f => $"{f.PropertyName}: {f.ErrorMessage}")));

            throw new ValidationException(failures);   // обробник не викличеться
        }

        return await next(ct);
    }
}

/// <summary>
/// Логує початок і завершення кожної операції MediatR.
/// </summary>
/// <remarks>
/// Саме за цим логом перевіряється контрольна точка методички: при помилці
/// валідації в лозі є «Handling …», але НЕМАЄ «Handled …» — отже, обробник
/// справді не виконувався.
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var name = typeof(TRequest).Name;
        logger.LogInformation("Handling {RequestName}", name);

        var response = await next(ct);

        logger.LogInformation("Handled {RequestName}", name);
        return response;
    }
}

/// <summary>
/// Вимірює тривалість операції та попереджає про аномально повільні запити
/// (зокрема до MongoDB).
/// </summary>
public sealed class PerformanceBehavior<TRequest, TResponse>(
    ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private const int SlowRequestThresholdMs = 500;

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await next(ct);
        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds > SlowRequestThresholdMs)
            logger.LogWarning("Slow request {RequestName}: {Elapsed} ms",
                typeof(TRequest).Name, stopwatch.ElapsedMilliseconds);
        else
            logger.LogDebug("{RequestName} completed in {Elapsed} ms",
                typeof(TRequest).Name, stopwatch.ElapsedMilliseconds);

        return response;
    }
}

/// <summary>
/// Централізоване ЛОГУВАННЯ винятків на рівні MediatR.
/// </summary>
/// <remarks>
/// Виняток не поглинається — він летить далі, а перетворення на HTTP-статус
/// робить глобальний обробник у WebApi. Тут лише журналювання з контекстом
/// операції, щоб інцидент можна було відтворити.
/// </remarks>
public sealed class ExceptionHandlingBehavior<TRequest, TResponse>(
    ILogger<ExceptionHandlingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        try
        {
            return await next(ct);
        }
        catch (ValidationException)
        {
            throw;                                  // уже залоговано у ValidationBehavior
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in {RequestName}", typeof(TRequest).Name);
            throw;
        }
    }
}
