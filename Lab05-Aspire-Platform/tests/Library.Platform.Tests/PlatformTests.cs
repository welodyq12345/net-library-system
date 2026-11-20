using System.Net;
using System.Text.Json;
using Library.Platform.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Library.Platform.Tests;

/* =============================================================================
   Тести наскрізних механізмів платформи: CorrelationId (Частина 4.2),
   склад ServiceDefaults (Частина 4.1) і конфігурація маршрутів YARP
   (Частина 5.1).
   ============================================================================= */

public class CorrelationIdMiddlewareTests
{
    private static DefaultHttpContext Context(string? incoming = null)
    {
        var context = new DefaultHttpContext();
        if (incoming is not null)
            context.Request.Headers[CorrelationId.HeaderName] = incoming;
        return context;
    }

    private static CorrelationIdMiddleware Middleware(RequestDelegate next) =>
        new(next, NullLogger<CorrelationIdMiddleware>.Instance);

    [Fact]
    public async Task Генерує_новий_ідентифікатор_коли_заголовка_немає()
    {
        var context = Context();
        string? seenByNext = null;

        await Middleware(ctx =>
        {
            seenByNext = ctx.Request.Headers[CorrelationId.HeaderName].ToString();
            return Task.CompletedTask;
        }).InvokeAsync(context);

        // записаний у ЗАПИТ -> його побачать і YARP, і типізовані HttpClient-и
        Assert.True(Guid.TryParse(seenByNext, out _));

        // і повернений клієнту у ВІДПОВІДІ
        Assert.Equal(seenByNext, context.Response.Headers[CorrelationId.HeaderName].ToString());
    }

    [Fact]
    public async Task Зберігає_ідентифікатор_клієнта()
    {
        const string incoming = "11111111-2222-3333-4444-555555555555";
        var context = Context(incoming);

        await Middleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.Equal(incoming, context.Request.Headers[CorrelationId.HeaderName].ToString());
        Assert.Equal(incoming, context.Response.Headers[CorrelationId.HeaderName].ToString());
    }

    [Fact]
    public async Task Порожній_заголовок_вважається_відсутнім()
    {
        var context = Context("   ");

        await Middleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.True(Guid.TryParse(
            context.Response.Headers[CorrelationId.HeaderName].ToString(), out _));
    }

    [Fact]
    public async Task Додає_CorrelationId_у_ВСІ_логи_запиту()
    {
        // саме завдяки LogContext у Dashboard можна відфільтрувати
        // логи одного звернення по всіх сервісах ланцюжка
        var sink = new CollectingSink();

        // ЛОКАЛЬНИЙ логер, а не Log.Logger: тестові класи xunit виконуються
        // паралельно, тому глобальний статичний логер підмінювати не можна.
        // LogContext від цього не страждає — він ambient (AsyncLocal).
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        const string incoming = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        await Middleware(_ =>
        {
            // лог, написаний ГЛИБОКО в обробнику запиту
            logger.Information("десь у бізнес-логіці");
            return Task.CompletedTask;
        }).InvokeAsync(Context(incoming));

        var entry = Assert.Single(sink.Events);
        Assert.True(entry.Properties.TryGetValue("CorrelationId", out var value));
        Assert.Contains(incoming, value!.ToString());
    }

    [Fact]
    public async Task У_відповіді_ЛИШЕ_ОДИН_заголовок_CorrelationId()
    {
        /* Регресія: Gateway повертав X-Correlation-Id двічі. YARP ДОДАЄ
           заголовки проксованої відповіді, а мікросервіс повертає той самий
           ідентифікатор, тому до записаного шлюзом значення додавалося друге.
           Виправлення — присвоєння в OnStarting, яке виконується останнім. */
        var feature = new RecordingResponseFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(feature);

        await Middleware(_ => Task.CompletedTask).InvokeAsync(context);

        // імітуємо YARP: до вже записаного заголовка додається значення з бекенда
        feature.Headers.Append(CorrelationId.HeaderName, "значення-від-мікросервісу");
        Assert.Equal(2, feature.Headers[CorrelationId.HeaderName].Count);

        // ...і перед відправкою відповіді спрацьовує OnStarting
        await feature.FireOnStartingAsync();

        Assert.Single(feature.Headers[CorrelationId.HeaderName]);
    }

    /// <summary>
    /// Відповідь, яка ЗАПАМ'ЯТОВУЄ зворотні виклики <c>OnStarting</c>:
    /// стандартна реалізація в <see cref="DefaultHttpContext"/> їх відкидає.
    /// </summary>
    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];

        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state)
            => _onStarting.Add((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state) { }

        public async Task FireOnStartingAsync()
        {
            HasStarted = true;
            foreach (var (callback, state) in _onStarting)
                await callback(state);
        }
    }

    private sealed class CollectingSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}

public class CorrelationIdHandlerTests
{
    /// <summary>Обробник, що фіксує вихідний запит і не йде в мережу.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, CapturingHandler Inner) CreateClient(HttpContext? context)
    {
        var accessor = new HttpContextAccessor { HttpContext = context };
        var inner = new CapturingHandler();
        var handler = new CorrelationIdHandler(accessor) { InnerHandler = inner };

        return (new HttpClient(handler) { BaseAddress = new Uri("http://catalog-api") }, inner);
    }

    [Fact]
    public async Task Переносить_CorrelationId_у_вихідний_запит()
    {
        const string id = "99999999-8888-7777-6666-555555555555";

        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationId.HeaderName] = id;

        var (client, inner) = CreateClient(context);
        await client.GetAsync("/api/books/1");

        Assert.Equal(id, inner.Request!.Headers.GetValues(CorrelationId.HeaderName).Single());
    }

    [Fact]
    public async Task Нічого_не_додає_поза_HTTP_запитом()
    {
        // фонова задача без HttpContext: заголовка просто не буде
        var (client, inner) = CreateClient(context: null);
        await client.GetAsync("/api/books/1");

        Assert.False(inner.Request!.Headers.Contains(CorrelationId.HeaderName));
    }

    [Fact]
    public async Task Не_перезаписує_вже_заданий_заголовок()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationId.HeaderName] = "from-context";

        var (client, inner) = CreateClient(context);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/books/1");
        request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, "explicit");
        await client.SendAsync(request);

        Assert.Equal("explicit",
            inner.Request!.Headers.GetValues(CorrelationId.HeaderName).Single());
    }
}

public class ServiceDefaultsTests
{
    private static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateApplicationBuilder();

        // AddServiceDefaults — узагальнений метод для будь-якого
        // IHostApplicationBuilder, тому працює і без WebApplication
        builder.AddServiceDefaults();
        return builder;
    }

    [Fact]
    public void AddServiceDefaults_Реєструє_health_check_живучості()
    {
        using var host = CreateBuilder().Build();

        var service = host.Services.GetRequiredService<HealthCheckService>();
        Assert.NotNull(service);

        var registrations = host.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        var self = Assert.Single(registrations, r => r.Name == "self");
        Assert.Contains("live", self.Tags);
    }

    [Fact]
    public void AddServiceDefaults_Реєструє_обробник_CorrelationId_для_HttpClient()
    {
        using var host = CreateBuilder().Build();

        // обробник має бути доступний, інакше ConfigureHttpClientDefaults
        // впаде під час створення клієнта
        Assert.NotNull(host.Services.GetRequiredService<CorrelationIdHandler>());
        Assert.NotNull(host.Services.GetRequiredService<IHttpContextAccessor>());
    }

    [Fact]
    public void AddServiceDefaults_Робить_Serilog_провайдером_логування()
    {
        using var host = CreateBuilder().Build();

        // Serilog зареєстрований у DI як ILogger самого Serilog...
        Assert.NotNull(host.Services.GetRequiredService<Serilog.ILogger>());

        // ...і стоїть за стандартним ILogger<T>, тому бізнес-код і далі
        // пише через Microsoft.Extensions.Logging, нічого не знаючи про Serilog
        var logger = host.Services
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ServiceDefaultsTests>>();

        Assert.NotNull(logger);
        logger.LogInformation("структурована властивість {Value}", 42);
    }

    [Fact]
    public void Типізовані_клієнти_отримують_CorrelationIdHandler_і_service_discovery()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("catalog",
            c => c.BaseAddress = new Uri("https+http://catalog-api"));

        using var host = builder.Build();

        var factory = host.Services.GetRequiredService<IHttpClientFactory>();

        // сам факт успішного створення означає, що і DelegatingHandler,
        // і резолвер service discovery зібралися в конвеєр
        var client = factory.CreateClient("catalog");
        Assert.Equal("https+http://catalog-api/", client.BaseAddress!.ToString());
    }
}

public class GatewayRouteConfigurationTests
{
    private static IConfigurationRoot GatewayConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile("gateway-appsettings.json", optional: false)
            .Build();

    private static JsonDocument GatewayJson() =>
        JsonDocument.Parse(File.ReadAllText("gateway-appsettings.json"));

    [Fact]
    public void Кожен_мікросервіс_має_власний_кластер()
    {
        var config = GatewayConfiguration();

        var clusters = config.GetSection("ReverseProxy:Clusters")
            .GetChildren().Select(c => c.Key).ToList();

        Assert.Contains("lending", clusters);
        Assert.Contains("catalog", clusters);
        Assert.Contains("reviews", clusters);
        Assert.Contains("aggregator", clusters);
    }

    [Fact]
    public void Адреси_кластерів_це_ЛОГІЧНІ_імена_а_не_порти()
    {
        var config = GatewayConfiguration();

        var addresses = config.GetSection("ReverseProxy:Clusters").GetChildren()
            .SelectMany(c => c.GetSection("Destinations").GetChildren())
            .Select(d => d["Address"]!)
            .ToList();

        Assert.NotEmpty(addresses);
        Assert.All(addresses, a =>
        {
            Assert.DoesNotContain("localhost", a);
            Assert.Matches(@"^https?(\+http)?://[a-z-]+$", a);
        });
    }

    [Fact]
    public void Усі_маршрути_посилаються_на_наявні_кластери()
    {
        var config = GatewayConfiguration();

        var clusters = config.GetSection("ReverseProxy:Clusters")
            .GetChildren().Select(c => c.Key).ToHashSet();

        var routes = config.GetSection("ReverseProxy:Routes").GetChildren().ToList();

        Assert.NotEmpty(routes);
        Assert.All(routes, r => Assert.Contains(r["ClusterId"]!, clusters));
    }

    [Fact]
    public void Специфічніші_маршрути_мають_вищий_пріоритет()
    {
        var config = GatewayConfiguration();
        var routes = config.GetSection("ReverseProxy:Routes");

        int Order(string name) => int.Parse(routes.GetSection(name)["Order"]!);

        // /api/lending/books має перехопити запит раніше за /api/books,
        // інакше проєкція Сервісу видач стане недосяжною
        Assert.True(Order("lending-books") < Order("books"));

        // Агрегатор — найспецифічніший префікс, тому найвищий пріоритет
        Assert.True(Order("aggregator") < Order("lending-books"));
    }

    [Fact]
    public void Маршрути_використовують_catch_all_шаблони()
    {
        var config = GatewayConfiguration();

        var paths = config.GetSection("ReverseProxy:Routes").GetChildren()
            .Select(r => r.GetSection("Match")["Path"]!)
            .ToList();

        Assert.All(paths, p =>
        {
            Assert.StartsWith("/api/", p);
            Assert.EndsWith("/{**catch-all}", p);
        });
    }

    [Fact]
    public void Кожен_маршрут_має_метадані_команди_та_версії()
    {
        var config = GatewayConfiguration();

        foreach (var route in config.GetSection("ReverseProxy:Routes").GetChildren())
        {
            Assert.False(string.IsNullOrWhiteSpace(route.GetSection("Metadata")["Team"]),
                $"маршрут {route.Key} без метаданих Team");
            Assert.Equal("v1", route.GetSection("Metadata")["Version"]);
        }
    }

    [Fact]
    public void Кластери_мають_обмежений_таймаут()
    {
        // типове значення YARP завелике: недоступний сервіс тримав би
        // запит клієнта надто довго
        var config = GatewayConfiguration();

        foreach (var cluster in config.GetSection("ReverseProxy:Clusters").GetChildren())
        {
            var timeout = cluster.GetSection("HttpRequest")["ActivityTimeout"];
            Assert.False(string.IsNullOrWhiteSpace(timeout),
                $"кластер {cluster.Key} без ActivityTimeout");
            Assert.True(TimeSpan.Parse(timeout!) <= TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public void Конфігурація_не_містить_жорстко_прописаних_адрес()
    {
        using var json = GatewayJson();

        // перевіряється САМА конфігурація проксі, а не пояснювальні комментарі
        var raw = json.RootElement.GetProperty("ReverseProxy").GetRawText();

        Assert.DoesNotContain("localhost", raw);
        Assert.DoesNotContain("127.0.0.1", raw);
        Assert.DoesNotContain(":5", raw);          // жодних портів
    }
}
