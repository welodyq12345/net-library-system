using System.Diagnostics;
using Library.Platform.ServiceDefaults;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

/* =============================================================================
   API Gateway — ЄДИНА ТОЧКА ВХОДУ платформи (Частина 5).

   Клієнт знає один порт (5000) і не знає ні про кількість мікросервісів, ні
   про їхні порти. Маршрутизацію виконує YARP (Yet Another Reverse Proxy) за
   конфігурацією з appsettings.json — секція ReverseProxy.

   Чому Gateway — ОКРЕМИЙ проєкт, а не готова інтеграція builder.AddYarp(...)
   в AppHost: тут є власний конвеєр (CorrelationId + логування рішень про
   маршрутизацію), а в AppHost описується лише інфраструктура.
   ============================================================================= */

var builder = WebApplication.CreateBuilder(args);

// логування, трасування, health checks, service discovery, стійкі HttpClient-и
builder.AddServiceDefaults();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    // "http://lending-api" -> реальна адреса, яку призначив Aspire.
    // Без цього рядка YARP спробував би розв'язати ім'я через DNS і не знайшов би його.
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

/* -----------------------------------------------------------------------------
   CorrelationId — ДО MapReverseProxy.
   Той самий middleware, що й у мікросервісах (ServiceDefaults, Частина 4.2):
   окремої реалізації для Gateway не потрібно. Тут він генерує ідентифікатор
   у більшості випадків, бо Gateway — перша точка входу запиту в систему.
   YARP копіює заголовки вхідного запиту в проксований, тому записаний у
   Request.Headers ідентифікатор сам дійде до мікросервісу.
   ----------------------------------------------------------------------------- */
app.UseServiceDefaults();

/* -----------------------------------------------------------------------------
   Логування рішень про маршрутизацію (Частина 5.3).
   Записується структурованими властивостями: на який маршрут і кластер
   потрапив запит, статус, тривалість і розмір відповіді. Саме цей лог
   відповідає на питання «чому запит пішов не туди».
   ----------------------------------------------------------------------------- */
app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();

    await next();

    stopwatch.Stop();

    // фічу маршрутизації YARP додає вже ПІСЛЯ вибору маршруту,
    // тому читаємо її на зворотному шляху конвеєра
    var proxyFeature = context.Features.Get<IReverseProxyFeature>();
    var route = proxyFeature?.Route?.Config;

    app.Logger.LogInformation(
        "{Method} {Path} -> route {Route} (cluster {Cluster}, team {Team}): "
        + "{StatusCode} in {Elapsed} ms, {ResponseLength} bytes",
        context.Request.Method,
        context.Request.Path.Value,
        route?.RouteId ?? "(none)",
        route?.ClusterId ?? "(none)",
        route?.Metadata?.GetValueOrDefault("Team") ?? "(none)",
        context.Response.StatusCode,
        stopwatch.ElapsedMilliseconds,
        context.Response.ContentLength ?? 0);

    // недоступний сервіс: YARP ставить 502/503/504 і причину в ProxyErrorFeature
    var error = context.Features.Get<IForwarderErrorFeature>();
    if (error is not null)
        app.Logger.LogError(error.Exception,
            "Проксування на маршрут {Route} не вдалося: {Error}",
            route?.RouteId ?? "(none)", error.Error);
});

app.MapReverseProxy();

// /alive і /health самого шлюзу (власного /health у Gateway немає,
// тому шлях за замовчуванням не конфліктує)
app.MapDefaultEndpoints();

// маленька «вітрина» платформи: перелік маршрутів у відповіді на корінь
app.MapGet("/", (IProxyConfigProvider provider) =>
{
    var config = provider.GetConfig();

    return Results.Ok(new
    {
        service = "Library.Platform.ApiGateway",
        description = "Єдина точка входу платформи «Система управління бібліотекою»",
        routes = config.Routes
            .OrderBy(r => r.Order ?? int.MaxValue)
            .Select(r => new
            {
                route = r.RouteId,
                path = r.Match.Path,
                cluster = r.ClusterId,
                order = r.Order
            }),
        clusters = config.Clusters.Select(c => new
        {
            cluster = c.ClusterId,
            destinations = c.Destinations?.Values.Select(d => d.Address),
            loadBalancing = c.LoadBalancingPolicy ?? "PowerOfTwoChoices (default)"
        })
    });
});

app.Run();
