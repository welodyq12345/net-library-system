using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace Library.Platform.ServiceDefaults;

/// <summary>
/// Наскрізний ідентифікатор звернення.
/// </summary>
/// <remarks>
/// <b>CorrelationId ≠ TraceId.</b> CorrelationId створює й передає сам
/// застосунок — це бізнес-ідентифікатор звернення, який можна назвати в
/// підтримці. TraceId автоматично генерує OpenTelemetry для технічного
/// трасування. У логах присутні обидва.
/// </remarks>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
}

/// <summary>
/// Middleware: бере <c>X-Correlation-Id</c> із запиту або створює новий,
/// кладе його в <c>LogContext</c> і повертає клієнту в заголовку відповіді.
/// </summary>
/// <remarks>
/// Реєструється в КОЖНОМУ сервісі, а не лише в Gateway: до сервісу можуть
/// звернутися напряму, в обхід шлюзу, і тоді ідентифікатор треба створити
/// на місці.
/// </remarks>
public sealed class CorrelationIdMiddleware(
    RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[CorrelationId.HeaderName].ToString();
        var generated = string.IsNullOrWhiteSpace(incoming);
        var id = generated ? Guid.NewGuid().ToString() : incoming;

        if (generated)
            // далі його побачать і YARP, і типізовані HttpClient-и
            context.Request.Headers[CorrelationId.HeaderName] = id;

        context.Response.Headers[CorrelationId.HeaderName] = id;

        // ...і ще раз — в OnStarting.
        // Причина: у Gateway відповідь формує YARP, який ДОДАЄ заголовки
        // проксованої відповіді — а мікросервіс повертає той самий
        // X-Correlation-Id, тому лише записаного наперед заголовка було б
        // два однакових значення. OnStarting виконується останнім,
        // безпосередньо перед відправкою, і присвоєння замінює всі попередні.
        context.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse, string))state;
            response.Headers[CorrelationId.HeaderName] = value;
            return Task.CompletedTask;
        }, (context.Response, id));

        using (LogContext.PushProperty("CorrelationId", id))
        {
            if (generated)
                logger.LogDebug("Generated new CorrelationId {CorrelationId}", id);

            logger.LogInformation("Request started {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await next(context);

            logger.LogInformation("Request finished with {StatusCode}",
                context.Response.StatusCode);
        }
    }
}

/// <summary>
/// Додає поточний <c>X-Correlation-Id</c> у ВИХІДНІ HTTP-запити.
/// </summary>
/// <remarks>
/// Підключається до всіх <c>HttpClient</c> через
/// <c>ConfigureHttpClientDefaults</c>, тому Aggregator передає ідентифікатор
/// далі в мікросервіси автоматично — писати це в кожному клієнті не потрібно.
/// </remarks>
public sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = accessor.HttpContext?.Request.Headers[CorrelationId.HeaderName].ToString();

        if (!string.IsNullOrWhiteSpace(id) && !request.Headers.Contains(CorrelationId.HeaderName))
            request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, id);

        return base.SendAsync(request, cancellationToken);
    }
}
