using System.Reflection;
using FluentValidation;
using Library.Catalog.Api.Infrastructure;
using Library.Catalog.Bll.Abstractions;
using Library.Catalog.Bll.Mapping;
using Library.Catalog.Bll.Services;
using Library.Catalog.Bll.Validators;
using Library.Catalog.Dal;
using Library.Catalog.Dal.Abstractions;
using Library.Platform.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

/* =============================================================================
   1. Конфігурація. Рядок підключення можна перевизначити змінною оточення:
        ConnectionStrings__CatalogDb=Server=...;Database=LibraryCatalogDb;...
   ============================================================================= */
var connectionString = builder.Configuration.GetConnectionString("CatalogDb")
    ?? throw new InvalidOperationException("Connection string 'CatalogDb' is missing");

/* =============================================================================
   2. Платформні налаштування (Лаб. 05).

   Один рядок замінює власну конфігурацію логування: ServiceDefaults дає
   Serilog зі структурованими логами, OpenTelemetry (трасування вхідних
   запитів, вихідних HTTP-викликів і звернень до БД), health checks,
   service discovery і стійкі HttpClient-и — однаково в усіх компонентах
   платформи.

   Поза платформою сервіс працює так само: рівні й sink-и логування
   по-старому читаються з appsettings.json.
   ============================================================================= */
builder.AddServiceDefaults();

/* =============================================================================
   3. EF Core: DbContext + Unit of Work.
   ============================================================================= */
builder.Services.AddDbContext<CatalogDbContext>(options =>
{
    options.UseSqlServer(connectionString, sql =>
    {
        sql.MigrationsAssembly(typeof(CatalogDbContext).Assembly.FullName);
        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5),
                                 errorNumbersToAdd: null);
    });

    if (builder.Environment.IsDevelopment())
    {
        // показувати значення параметрів у логах SQL — лише в розробці
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

/* =============================================================================
   4. Бізнес-логіка, мапінг і валідація.
      AutoMapper 15.0+ потребує ключа безкоштовної Community-ліцензії
      (automapper.io); без нього застосунок працює, але пише в лог
      повідомлення про відсутню ліцензію.
   ============================================================================= */
builder.Services.AddAutoMapper(cfg =>
{
    cfg.LicenseKey = builder.Configuration["AutoMapper:LicenseKey"];
    cfg.AddProfile<CatalogProfile>();
});

builder.Services.AddScoped<IBookService, BookService>();
builder.Services.AddScoped<IAuthorService, AuthorService>();
builder.Services.AddScoped<IGenreService, GenreService>();

// Валідатори реєструються через FluentValidation.DependencyInjectionExtensions
// і викликаються ЯВНО в контролерах: автоматична валідація через
// FluentValidation.AspNetCore застаріла й більше не підтримується.
builder.Services.AddValidatorsFromAssemblyContaining<CreateBookDtoValidator>();

/* =============================================================================
   5. Web/API: контролери, ProblemDetails, глобальний обробник винятків.
   ============================================================================= */
builder.Services.AddControllers();

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        ctx.ProblemDetails.Extensions["service"] = "Library.Catalog.Api";
    });

builder.Services.AddExceptionHandler<DomainExceptionHandler>();

/* =============================================================================
   6. OpenAPI / Swagger.
   ============================================================================= */
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Library Catalog API",
        Version = "v1",
        Description = """
            Мікросервіс «Каталог книг» системи управління бібліотекою.
            Тришарова архітектура на Entity Framework Core (Code-First):
            Fluent API, міграції, ідемпотентний сідінг, Repository / Unit of Work,
            Specification pattern (Ardalis), пагінація та фільтрація на рівні БД.

            Три способи завантаження пов'язаних даних:
              • Eager Loading   — GET /api/books/{id}            (один SQL-запит)
              • Explicit Loading— GET /api/authors/{id}?includeBooks=true (два запити)
              • LINQ через M:N  — GET /api/genres/{name}/recent-books

            Помилки — у форматі ProblemDetails (RFC 9457):
            404 — ресурс відсутній, 409 — порушення інваріанту сутності або
            конфлікт унікальності, 400 — валідація (ValidationProblemDetails).
            """
    });

    var xml = Path.Combine(AppContext.BaseDirectory,
        $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
        options.IncludeXmlComments(xml, includeControllerXmlComments: true);
});

var app = builder.Build();

/* =============================================================================
   7. Міграції та ідемпотентний сідінг.
      Сідінг сумісний із CI/CD: його можна вимкнути в production
      (Database:SeedOnStartup = false).
   ============================================================================= */
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Застосовую міграції EF Core...");
    await db.Database.MigrateAsync();

    if (!app.Environment.IsProduction()
        && app.Configuration.GetValue("Database:SeedOnStartup", true))
    {
        await DbInitializer.SeedAsync(db, logger);
    }
}

/* =============================================================================
   8. Конвеєр обробки запитів.
   ============================================================================= */
app.UseExceptionHandler();
// CorrelationId + логування запитів (ServiceDefaults): наскрізний
// ідентифікатор потрапляє в LogContext, у заголовок відповіді та у вихідні
// HTTP-виклики — так один запит видно в логах усіх сервісів ланцюжка
app.UseServiceDefaults();

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Library Catalog API v1");
    o.DocumentTitle = "Library Catalog API";
});

app.MapControllers();

// /alive (живучість) і /health/ready (усі перевірки) від ServiceDefaults.
// Власний /health нижче лишається: він перевіряє саме базу цього сервісу,
// а двічі зареєстрований однаковий маршрут дав би AmbiguousMatchException.
app.MapDefaultEndpoints("/health/ready");

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.MapGet("/health", async (CatalogDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "Healthy", service = "Library.Catalog.Api" })
        : Results.Problem("Database is unreachable", statusCode: StatusCodes.Status503ServiceUnavailable);
})
.WithName("Health")
.WithTags("Diagnostics");

try
{
    Log.Information("Starting Library.Catalog.Api");
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Потрібен для інтеграційних тестів через <c>WebApplicationFactory</c>.</summary>
public partial class Program { }
