using System.Reflection;
using Library.Platform.ServiceDefaults;
using Library.Reviews.Application;
using Library.Reviews.Infrastructure;
using Library.Reviews.Infrastructure.Persistence;
using Library.Reviews.WebApi.Infrastructure;
using MongoDB.Driver;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

/* =============================================================================
   Presentation-шар Clean Architecture.

   Program.cs — єдине місце, де Infrastructure взагалі згадується, і лише
   заради реєстрації залежностей під час старту. Контролери знають тільки
   про Application (команди, запити й DTO) і про MediatR.
   ============================================================================= */

/* =============================================================================
   Платформні налаштування (Лаб. 05).

   Один рядок замінює власну конфігурацію логування: ServiceDefaults дає
   Serilog зі структурованими логами, OpenTelemetry (трасування вхідних
   запитів, вихідних HTTP-викликів і звернень до БД), health checks,
   service discovery і стійкі HttpClient-и — однаково в усіх компонентах
   платформи.

   Поза платформою сервіс працює так само: рівні й sink-и логування
   по-старому читаються з appsettings.json.
   ============================================================================= */
builder.AddServiceDefaults();

// --- шари ---
builder.Services
    .AddApplication(builder.Configuration)       // MediatR, Pipeline Behaviors, валідатори
    .AddInfrastructure(builder.Configuration);   // MongoDB, репозиторії, індекси, сідінг

// --- Web/API ---
builder.Services.AddControllers();

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        ctx.ProblemDetails.Extensions["service"] = "Library.Reviews.WebApi";
    });

builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Library Reviews API",
        Version = "v1",
        Description = """
            Мікросервіс «Відгуки та обговорення» системи управління бібліотекою.

            Clean Architecture: Domain -> Application -> Infrastructure -> WebApi.
            Domain не залежить від жодного іншого шару й не знає про MongoDB.

            CQRS через MediatR: кожна операція — окрема команда або запит
            з власним обробником. Валідація виконується Pipeline Behavior
            ДО виклику обробника.

            MongoDB: вбудовані об'єкти (author), вбудовані масиви (comments),
            посилання (discussionId), денормалізація (bookTitle), гнучка схема
            (photos, tags є не в усіх документів), оптимістична конкурентність
            через поле version та заголовки ETag / If-Match.

            Помилки — ProblemDetails (RFC 9457): 404, 400, 409 (у т. ч.
            конфлікт версій і DuplicateKey), 503 при недоступності бази.
            """
    });

    var xml = Path.Combine(AppContext.BaseDirectory,
        $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
        options.IncludeXmlComments(xml, includeControllerXmlComments: true);
});

var app = builder.Build();

app.UseExceptionHandler();
// CorrelationId + логування запитів (ServiceDefaults): наскрізний
// ідентифікатор потрапляє в LogContext, у заголовок відповіді та у вихідні
// HTTP-виклики — так один запит видно в логах усіх сервісів ланцюжка
app.UseServiceDefaults();

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Library Reviews API v1");
    o.DocumentTitle = "Library Reviews API";
});

app.MapControllers();

// /alive (живучість) і /health/ready (усі перевірки) від ServiceDefaults.
// Власний /health нижче лишається: він перевіряє саме базу цього сервісу,
// а двічі зареєстрований однаковий маршрут дав би AmbiguousMatchException.
app.MapDefaultEndpoints("/health/ready");

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// health-ендпоінт: перевіряє, що MongoDB відповідає на ping
app.MapGet("/health", async (MongoDbContext context, CancellationToken ct) =>
{
    try
    {
        await context.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: ct);

        return Results.Ok(new { status = "Healthy", service = "Library.Reviews.WebApi" });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "MongoDB is unreachable",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("Health")
.WithTags("Diagnostics");

try
{
    Log.Information("Starting Library.Reviews.WebApi");
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Потрібен для інтеграційних тестів через <c>WebApplicationFactory</c>.</summary>
public partial class Program { }
