using Library.Reviews.Domain.Entities;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Extensions.DiagnosticSources;

namespace Library.Reviews.Infrastructure.Persistence;

/// <summary>Налаштування підключення до MongoDB (секція <c>MongoDb</c>).</summary>
public sealed class MongoDbOptions
{
    public const string SectionName = "MongoDb";

    public string ConnectionString { get; set; } = "";
    public string DatabaseName { get; set; } = "";
}

/// <summary>
/// Контекст MongoDB: єдина точка отримання колекцій.
/// </summary>
/// <remarks>
/// Живе в Infrastructure — внутрішні шари про нього не знають.
/// Реєструється як <b>Singleton</b>: <see cref="MongoClient"/> потокобезпечний
/// і сам керує пулом з'єднань, тому створювати його на кожен запит не можна.
/// Параметри пулу й таймаутів задаються в рядку підключення
/// (<c>maxPoolSize</c>, <c>serverSelectionTimeoutMS</c>).
/// </remarks>
public sealed class MongoDbContext
{
    public MongoDbContext(IOptions<MongoDbOptions> options)
    {
        var value = options.Value;

        if (string.IsNullOrWhiteSpace(value.ConnectionString))
            throw new InvalidOperationException("MongoDb:ConnectionString is not configured");

        var settings = MongoClientSettings.FromConnectionString(value.ConnectionString);
        settings.ApplicationName = "Library.Reviews";

        // Трасування MongoDB (Лаб. 05, ч. 4.3): драйвер публікує події кластера,
        // а підписник перетворює їх на Activity -> spans із командою та
        // колекцією видно в Aspire Dashboard поряд зі spans HTTP і SQL.
        // Без слухача (поза платформою) підписка не має жодного ефекту.
        settings.ClusterConfigurator = cb => cb.Subscribe(new DiagnosticsActivityEventSubscriber());

        Client = new MongoClient(settings);

        var databaseName = string.IsNullOrWhiteSpace(value.DatabaseName)
            ? MongoUrl.Create(value.ConnectionString).DatabaseName
            : value.DatabaseName;

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("MongoDb:DatabaseName is not configured");

        Database = Client.GetDatabase(databaseName);
    }

    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }

    /// <summary>Колекція <c>reviews</c> — та сама, що описана в Лаб. 01.</summary>
    public IMongoCollection<Review> Reviews => Database.GetCollection<Review>("reviews");

    /// <summary>Колекція <c>discussions</c>.</summary>
    public IMongoCollection<Discussion> Discussions
        => Database.GetCollection<Discussion>("discussions");
}
