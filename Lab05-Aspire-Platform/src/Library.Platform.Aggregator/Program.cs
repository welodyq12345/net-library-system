using System.Reflection;
using System.Text.Json.Serialization;
using Library.Platform.Aggregator.Clients;
using Library.Platform.Aggregator.Services;
using Library.Platform.ServiceDefaults;

/* =============================================================================
   Aggregator Service (Частина 6) — композиція даних кількох мікросервісів.

   Жодної власної бази: Агрегатор лише опитує сервіси по HTTP і складає з
   їхніх відповідей одну, зручну для клієнта. Тому в нього немає ні
   репозиторіїв, ні міграцій — лише типізовані клієнти й сервіс агрегації.
   ============================================================================= */

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

/* -----------------------------------------------------------------------------
   Typed HttpClients (Частина 6.1).

   "https+http://lending-api" — це НЕ адреса, а логічне ім'я ресурсу з AppHost:
     • схема https+http = «HTTPS, якщо сервіс його має, інакше HTTP»;
     • реальний хост і порт підставляє service discovery, увімкнений у
       ServiceDefaults через ConfigureHttpClientDefaults;
     • CorrelationId у вихідні запити додає CorrelationIdHandler — теж із
       ServiceDefaults, тому тут про нього згадувати не потрібно;
     • повтори, таймаути й circuit breaker дає AddStandardResilienceHandler.

   Фолбек на localhost у appsettings.json дозволяє запустити Агрегатор
   і без Aspire — для цього читається секція Services.
   ----------------------------------------------------------------------------- */
// Порожнє значення в конфігурації — це теж "не задано": у appsettings.json
// ключі секції Services лежать порожніми, і new Uri("") кинув би виняток
// при створенні клієнта, тобто вже під час обробки запиту.
Uri Address(string key, string logicalName)
{
    var configured = builder.Configuration[$"Services:{key}"];
    return new Uri(string.IsNullOrWhiteSpace(configured) ? logicalName : configured);
}

builder.Services.AddHttpClient<ILendingClient, LendingClient>(client =>
    client.BaseAddress = Address("Lending", "https+http://lending-api"));

builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
    client.BaseAddress = Address("Catalog", "https+http://catalog-api"));

builder.Services.AddHttpClient<IReviewsClient, ReviewsClient>(client =>
    client.BaseAddress = Address("Reviews", "https+http://reviews-api"));

builder.Services.AddScoped<IAggregationService, AggregationService>();

/* -----------------------------------------------------------------------------
   Web/API. JSON — camelCase (за замовчуванням у ASP.NET Core), null-поля
   не серіалізуються: у частковій відповіді відсутні блоки просто зникають,
   а не засмічують її значеннями null.
   ----------------------------------------------------------------------------- */
builder.Services.AddControllers();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        ctx.ProblemDetails.Extensions["service"] = "Library.Platform.Aggregator";
        ctx.ProblemDetails.Extensions["correlationId"] =
            ctx.HttpContext.Request.Headers[CorrelationId.HeaderName].ToString();
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Library Platform Aggregator",
        Version = "v1",
        Description = """
            Сервіс-агрегатор платформи «Система управління бібліотекою».

            Реалізує Aggregator Pattern: паралельно (Task.WhenAll) опитує
            Сервіс видач (Лаб. 02), Каталог (Лаб. 03) і Сервіс відгуків
            (Лаб. 04) та повертає одну консолідовану відповідь.

            Часткові відмови: недоступність одного сервісу не призводить до
            помилки 500 — відповідь містить зібрані дані, а причина потрапляє
            в масив warnings. Блок meta показує тривалість кожного виклику
            та виграш від паралельності.
            """
    });

    var xml = Path.Combine(AppContext.BaseDirectory,
        $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
        options.IncludeXmlComments(xml, includeControllerXmlComments: true);
});

var app = builder.Build();

// CorrelationId + логування запитів (ті самі, що в усіх компонентах платформи)
app.UseServiceDefaults();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Library Platform Aggregator v1");
    o.DocumentTitle = "Library Platform Aggregator";
});

app.MapControllers();
app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

/// <summary>Потрібен для інтеграційних тестів через <c>WebApplicationFactory</c>.</summary>
public partial class Program { }
