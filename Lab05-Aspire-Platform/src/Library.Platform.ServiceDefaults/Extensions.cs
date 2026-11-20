using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;              // UseOtlpExporter
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;    // ResourceBuilder.AddService
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Library.Platform.ServiceDefaults;

/// <summary>
/// Спільні налаштування всіх компонентів платформи: структуроване логування,
/// розподілене трасування, health checks, service discovery і стійкість
/// HTTP-клієнтів.
/// </summary>
/// <remarks>
/// Кожен мікросервіс, Gateway і Aggregator підключають цю бібліотеку одним
/// рядком — так логи й трасування скрізь однакові. Аналог
/// <c>eShop.ServiceDefaults</c>.
/// </remarks>
public static class ServiceDefaultsExtensions
{
    /// <summary>Підключає логування, трасування, health checks і service discovery.</summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureSerilog();
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddTransient<CorrelationIdHandler>();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Повтори, circuit breaker і таймаути (детально — Лаб. 07).
            // Значення за замовчуванням завеликі для платформи: 3 повтори з
            // експоненційною затримкою і загальним бюджетом 30 с означають, що
            // запит до ЗУПИНЕНОГО сервісу тримав би клієнта десятки секунд, і
            // Gateway віддав би 504 замість часткової відповіді Агрегатора.
            http.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);

                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds(200);

                // вимога бібліотеки: вікно спостереження >= 2 × AttemptTimeout
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
            });

            // "https+http://orders-api" -> реальна адреса, яку дав Aspire
            http.AddServiceDiscovery();

            // наскрізний CorrelationId у вихідних викликах
            http.AddHttpMessageHandler<CorrelationIdHandler>();
        });

        return builder;
    }

    /// <summary>
    /// Structured logging через Serilog.
    /// </summary>
    /// <remarks>
    /// <b>Ключова деталь:</b> <c>writeToProviders: true</c>. Вкладка
    /// «Structured logs» в Aspire Dashboard отримує логи по протоколу OTLP,
    /// а не з консолі. Якщо Serilog пише лише у свої sink-и, у Dashboard
    /// їх не буде. Цей прапорець передає записи далі стандартним провайдерам
    /// логування — серед них провайдер OpenTelemetry, який налаштовує
    /// <see cref="ConfigureOpenTelemetry"/>.
    /// </remarks>
    public static TBuilder ConfigureSerilog<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var serviceName = builder.Environment.ApplicationName;

        builder.Services.AddSerilog((services, cfg) =>
        {
            cfg.ReadFrom.Configuration(builder.Configuration)
               .MinimumLevel.Is(builder.Environment.IsDevelopment()
                   ? LogEventLevel.Debug
                   : LogEventLevel.Information)
               .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
               .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
               .Enrich.FromLogContext()
               .Enrich.WithProperty("ServiceName", serviceName)
               .Enrich.WithEnvironmentName()
               .Enrich.WithMachineName()
               .Enrich.WithThreadId()
               .WriteTo.Console(new CompactJsonFormatter());
        },
        writeToProviders: true);     // -> OpenTelemetry -> Structured logs у Dashboard

        return builder;
    }

    /// <summary>
    /// Розподілене трасування та метрики.
    /// </summary>
    /// <remarks>
    /// Інструментація SqlClient покриває одразу і Dapper (Лаб. 02), і EF Core
    /// (Лаб. 03) — обидва працюють через <c>Microsoft.Data.SqlClient</c>.
    /// Окреме джерело додано для драйвера MongoDB (Лаб. 04).
    /// </remarks>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.Environment.ApplicationName))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(t => t
                .AddSource(builder.Environment.ApplicationName)                 // власні spans
                .AddSource("MongoDB.Driver.Core.Extensions.DiagnosticSources")  // MongoDB (Лаб. 04)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    // health-перевірки не засмічують трасування
                    !ctx.Request.Path.StartsWithSegments("/health")
                    && !ctx.Request.Path.StartsWithSegments("/alive"))
                .AddHttpClientInstrumentation()
                // Текст запиту (db.query.text) драйвер пише сам; значення
                // параметрів у трасування НЕ потрапляють — там персональні дані.
                .AddSqlClientInstrumentation());

        // адресу колектора задає Aspire через OTEL_EXPORTER_OTLP_ENDPOINT
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Конвеєр: CorrelationId і логування HTTP-запитів.
    /// Викликається одразу після <c>builder.Build()</c>.
    /// </summary>
    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();

        app.UseSerilogRequestLogging(options =>
            options.EnrichDiagnosticContext = (diagnostic, http) =>
            {
                diagnostic.Set("CorrelationId",
                    http.Request.Headers[ServiceDefaults.CorrelationId.HeaderName].ToString());
                diagnostic.Set("ClientIp",
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            });

        return app;
    }

    /// <summary>
    /// Ендпоінти <c>/alive</c> (лише живучість) і <c>healthPath</c> (усі перевірки).
    /// </summary>
    /// <param name="app">Застосунок.</param>
    /// <param name="healthPath">
    /// Шлях для повної перевірки. Мікросервіси Лаб. 02–04 вже мають власний
    /// <c>/health</c>, який перевіряє свою базу, тому вони передають тут
    /// <c>/health/ready</c>: двічі зареєстрований однаковий маршрут призводить
    /// до AmbiguousMatchException під час запиту.
    /// </param>
    public static WebApplication MapDefaultEndpoints(
        this WebApplication app, string healthPath = "/health")
    {
        // "живий" != "готовий": /alive показує, що процес працює,
        // а healthPath — що всі залежності доступні.
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        app.MapHealthChecks(healthPath);

        return app;
    }
}
