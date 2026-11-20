using Library.Reviews.Application.Common.Interfaces;
using Library.Reviews.Application.Discussions;
using Library.Reviews.Domain.Interfaces;
using Library.Reviews.Infrastructure.Persistence;
using Library.Reviews.Infrastructure.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Library.Reviews.Infrastructure;

/// <summary>
/// Реєстрація шару Infrastructure: саме тут абстракції з внутрішніх шарів
/// звʼязуються з конкретними реалізаціями на MongoDB.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // BSON-мапінг має бути зареєстрований ДО першого звернення до бази
        MongoMappings.Register();

        services.Configure<MongoDbOptions>(configuration.GetSection(MongoDbOptions.SectionName));

        // MongoClient потокобезпечний і сам керує пулом з'єднань -> Singleton
        services.AddSingleton<MongoDbContext>();

        // сторона ЗАПИСУ — доменні інтерфейси
        services.AddScoped<IReviewRepository, ReviewRepository>();
        services.AddScoped<IDiscussionRepository, DiscussionRepository>();

        // сторона ЧИТАННЯ — інтерфейси з Application
        services.AddScoped<IReviewQueries, ReviewQueries>();
        services.AddScoped<IDiscussionQueries, DiscussionQueries>();

        // індекси + ідемпотентний сідінг під час старту
        services.AddSingleton<IDataSeeder, ReviewSeeder>();
        services.AddHostedService<IndexCreationService>();

        return services;
    }
}
