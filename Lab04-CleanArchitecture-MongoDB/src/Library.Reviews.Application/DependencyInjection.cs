using FluentValidation;
using Library.Reviews.Application.Common.Behaviors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Library.Reviews.Application;

/// <summary>
/// Реєстрація шару Application. Кожен шар надає власний метод розширення —
/// Program.cs лише складає їх докупи.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(cfg =>
        {
            // MediatR 13.0+ і AutoMapper 15.0+ розповсюджуються за комерційною
            // ліцензією з безкоштовною Community-редакцією (один ключ покриває
            // обидва пакети). Без ключа застосунок працює, лише пише в лог
            // повідомлення про його відсутність.
            var licenseKey = configuration["LuckyPenny:LicenseKey"];
            if (!string.IsNullOrWhiteSpace(licenseKey))
                cfg.LicenseKey = licenseKey;

            cfg.RegisterServicesFromAssembly(assembly);

            // ПОРЯДОК ВАЖЛИВИЙ: перший зареєстрований — найзовнішніший.
            // Logging бачить усе, зокрема й виняток валідації;
            // Performance міряє час уже самого обробника.
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));            // 1
            cfg.AddOpenBehavior(typeof(ExceptionHandlingBehavior<,>));  // 2
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));         // 3
            cfg.AddOpenBehavior(typeof(PerformanceBehavior<,>));        // 4 — біля обробника
        });

        // валідатори знаходяться автоматично; окремий пакет
        // MediatR.Extensions.Microsoft.DependencyInjection більше не потрібен —
        // AddMediatR вбудовано в сам MediatR починаючи з версії 12.
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        return services;
    }
}
