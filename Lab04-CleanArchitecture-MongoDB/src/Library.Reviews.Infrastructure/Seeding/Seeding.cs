using Library.Reviews.Domain.Entities;
using Library.Reviews.Domain.ValueObjects;
using Library.Reviews.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Library.Reviews.Infrastructure.Seeding;

/// <summary>Контракт наповнення колекції початковими даними.</summary>
public interface IDataSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

/// <summary>
/// Наповнення колекцій <c>reviews</c> і <c>discussions</c> даними з Лаб. 01.
/// </summary>
/// <remarks>
/// <b>Ідемпотентність:</b> якщо в колекції вже є документи, сідінг виходить
/// одразу. Тому повторний запуск застосунку дублікатів не створює.
/// Вставка — bulk-операцією <c>InsertManyAsync</c>.
/// </remarks>
public sealed class ReviewSeeder(MongoDbContext context, ILogger<ReviewSeeder> logger)
    : IDataSeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var existing = await context.Reviews.EstimatedDocumentCountAsync(cancellationToken: ct);
        if (existing > 0)
        {
            logger.LogInformation(
                "Сідінг пропущено: у колекції reviews вже {Count} документ(ів).", existing);
            return;
        }

        logger.LogInformation("Наповнюю колекції reviews і discussions початковими даними...");

        // --- обговорення (на них посилатимуться відгуки) ---
        var kobzarTalk = new Discussion(
            new BookRef(1, "Кобзар"),
            "Чи варто читати «Кобзар» у школі?");
        kobzarTalk.Post(new Author(1, "Олена К."), "Класика, яку варто перечитувати дорослим.");
        kobzarTalk.Post(new Author(2, "Андрій Ш."), "У школі складно, але з хорошим учителем — так.", replyTo: 0);
        kobzarTalk.Post(new Author(3, "Марія Б."), "Погоджуюсь, контекст важливіший за заучування.", replyTo: 1);

        var internatTalk = new Discussion(
            new BookRef(4, "Інтернат"),
            "Мова війни в романі Жадана");
        internatTalk.Post(new Author(null, "Ігор"), "Найсильніше — відсутність пафосу.");
        internatTalk.Post(new Author(1, "Олена К."), "Саме тому він і болить.", replyTo: 0);
        internatTalk.Close("Тему вичерпано");

        await context.Discussions.InsertManyAsync([kobzarTalk, internatTalk], cancellationToken: ct);

        // --- відгуки: різні набори полів в одній колекції (гнучка схема) ---

        // #1 ГІБРИД: вбудований автор + вбудовані коментарі + ПОСИЛАННЯ на обговорення
        var kobzar = new Review(
            new BookRef(1, "Кобзар"), new Author(1, "Олена К."), 5,
            "Видання з якісними примітками — читати набагато легше.");
        kobzar.AddComment(new Author(2, "Андрій Ш."), "Згоден, примітки врятували.");
        kobzar.AddComment(new Author(3, "Марія Б."), "А яке саме видання?");
        kobzar.AttachDiscussion(kobzarTalk.Id);

        // #2 ГНУЧКА СХЕМА: є photos і tags, яких немає в інших документів
        var internat = new Review(
            new BookRef(4, "Інтернат"), new Author(2, "Андрій Ш."), 5,
            "Прочитав за два вечори. Мова суха і від цього ще сильніша.");
        internat.AddPhoto("internat-cover.jpg");
        internat.AddPhoto("internat-page-77.jpg");
        internat.AddTag("війна");
        internat.AddTag("сучасна проза");
        internat.AddComment(new Author(null, "Ігор"), "Варто читати разом із «Ворошиловградом».");
        internat.AttachDiscussion(internatTalk.Id);

        // #3 АНОНІМНИЙ: немає readerId, немає коментарів і обговорення
        var tygrolovy = new Review(
            new BookRef(2, "Тигролови"), Author.Anonymous(), 4,
            "Динамічно, але фінал надто швидкий.");

        // #4 без тексту — лише оцінка (мінімальний документ)
        var misto = new Review(new BookRef(3, "Місто"), Author.Anonymous("Читач без профілю"), 3);

        // #5 звичайний відгук із коментарем
        var voron = new Review(
            new BookRef(5, "Чорний Ворон"), new Author(3, "Марія Б."), 4,
            "Сильний історичний матеріал, подекуди затягнуто.");
        voron.AddComment(new Author(1, "Олена К."), "Друга половина читається швидше.");

        await context.Reviews.InsertManyAsync(
            [kobzar, internat, tygrolovy, misto, voron], cancellationToken: ct);

        logger.LogInformation("Сідінг завершено: 5 відгуків і 2 обговорення.");
    }
}

/// <summary>
/// Створює індекси MongoDB під час старту застосунку.
/// </summary>
/// <remarks>
/// Повторне створення наявного індексу безпечне — MongoDB його не дублює,
/// тому сервіс можна перезапускати скільки завгодно.
/// Після створення індексів виконується ідемпотентний сідінг.
/// </remarks>
public sealed class IndexCreationService(
    MongoDbContext context,
    IDataSeeder seeder,
    ILogger<IndexCreationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            var reviewKeys = Builders<Review>.IndexKeys;

            await CreateIndexesAsync(context.Reviews, ct,
            [
                // «останні відгуки про книгу» — складений індекс
                new CreateIndexModel<Review>(
                    reviewKeys.Ascending("bookId").Descending(r => r.CreatedAt),
                    new CreateIndexOptions { Name = "ix_reviews_book_created" }),

                // «середній рейтинг і розподіл оцінок» — під aggregation
                new CreateIndexModel<Review>(
                    reviewKeys.Ascending("bookId").Descending("rating"),
                    new CreateIndexOptions { Name = "ix_reviews_book_rating" }),

                // повнотекстовий пошук
                new CreateIndexModel<Review>(
                    reviewKeys.Text(r => r.Text).Text("bookTitle"),
                    new CreateIndexOptions { Name = "ix_reviews_fulltext", DefaultLanguage = "none" }),

                // ОДИН відгук від читача на книгу — унікальний індекс.
                // PARTIAL, а не sparse: для СКЛАДЕНОГО індексу sparse виключає
                // документ лише тоді, коли відсутні ВСІ його поля. Оскільки
                // bookId є завжди, анонімні відгуки (readerId відсутній) все
                // одно потрапили б в індекс зі значенням null — і двох
                // анонімних відгуків на одну книгу вже не можна було б
                // створити. Частковий фільтр індексує тільки ті документи,
                // у яких readerId справді є.
                new CreateIndexModel<Review>(
                    reviewKeys.Ascending("bookId").Ascending("author.readerId"),
                    new CreateIndexOptions<Review>
                    {
                        Name = "ux_reviews_book_reader",
                        Unique = true,
                        PartialFilterExpression = Builders<Review>.Filter
                            .Exists("author.readerId", true)
                    })
            ]);

            var discussionKeys = Builders<Discussion>.IndexKeys;

            await CreateIndexesAsync(context.Discussions, ct,
            [
                new CreateIndexModel<Discussion>(
                    discussionKeys.Ascending("bookId").Ascending(d => d.IsClosed),
                    new CreateIndexOptions { Name = "ix_discussions_book_open" }),

                new CreateIndexModel<Discussion>(
                    discussionKeys.Descending(d => d.LastActivityAt),
                    new CreateIndexOptions { Name = "ix_discussions_activity", Sparse = true })
            ]);

            logger.LogInformation("Індекси MongoDB створено або вже існували.");

            await seeder.SeedAsync(ct);
        }
        catch (Exception ex)
        {
            // недоступна база не повинна валити старт застосунку:
            // health check покаже проблему, а запити повернуть 503
            logger.LogError(ex, "Не вдалося створити індекси або виконати сідінг MongoDB");
        }
    }


    /// <summary>
    /// Створює індекси по одному. Якщо індекс із таким іменем уже існує, але
    /// з ІНШИМИ опціями (MongoDB повертає IndexOptionsConflict / IndexKeySpecsConflict),
    /// він перестворюється — інакше зміна визначення індексу в коді вимагала б
    /// ручного втручання в базу.
    /// </summary>
    private static async Task CreateIndexesAsync<T>(
        IMongoCollection<T> collection, CancellationToken ct, CreateIndexModel<T>[] models)
    {
        foreach (var model in models)
        {
            try
            {
                await collection.Indexes.CreateOneAsync(model, cancellationToken: ct);
            }
            catch (MongoCommandException ex) when (ex.Code is 85 or 86)
            {
                var name = model.Options?.Name;
                if (name is null) throw;

                await collection.Indexes.DropOneAsync(name, ct);
                await collection.Indexes.CreateOneAsync(model, cancellationToken: ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
