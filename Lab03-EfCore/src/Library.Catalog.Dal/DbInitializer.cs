using Library.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Library.Catalog.Dal;

/// <summary>
/// Програмний (другий) спосіб сідінгу — доповнює статичний
/// <c>HasData</c> у конфігураціях.
/// </summary>
/// <remarks>
/// <para>
/// <b>Розподіл обовʼязків:</b>
/// <list type="bullet">
///   <item><b>Модельний (статичний) сідінг</b> — <c>HasData</c> у
///         <c>AuthorConfiguration</c> і <c>GenreConfiguration</c>: довідники з
///         фіксованими Id, якими керують міграції;</item>
///   <item><b>Програмний сідінг</b> — цей клас: сутності з навігаціями
///         (<c>Book</c> + <c>BookDetail</c> + <c>BookCopy</c> + <c>BookGenre</c>),
///         які через <c>HasData</c> задавати незручно.</item>
/// </list>
/// </para>
/// <para>
/// <b>ІДЕМПОТЕНТНІСТЬ:</b> перед вставкою перевіряється наявність запису за
/// бізнес-ключем (ISBN), тому повторний запуск застосунку дублікатів НЕ створює.
/// Дотримано цілісність FK: довідники (Authors, Genres) уже існують завдяки
/// міграції, книги додаються після них.
/// </para>
/// <para>
/// Сідінг сумісний із CI/CD — його можна вимкнути в production
/// (<c>Database:SeedOnStartup</c>).
/// </para>
/// </remarks>
public static class DbInitializer
{
    public static async Task SeedAsync(
        CatalogDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        // ідемпотентність: якщо книги вже є, нічого не робимо
        if (await db.Books.AnyAsync(ct))
        {
            logger?.LogInformation("Сідінг пропущено: каталог уже наповнено.");
            return;
        }

        logger?.LogInformation("Наповнюю каталог початковими даними...");

        var books = new[]
        {
            Create("Кобзар", "978-966-03-5555-1", 3.50m, authorId: 1, year: 1840,
                   description: "Збірка поетичних творів Тараса Шевченка, основа української літератури.",
                   publisher: "Дніпро", pages: 368,
                   copies: [("INV-0001", "A-01", CopyCondition.Good), ("INV-0002", "A-01", CopyCondition.New)],
                   genres: [(1, true), (4, false)]),

            Create("Тигролови", "978-966-03-5555-2", 4.00m, authorId: 2, year: 1944,
                   description: "Пригодницький роман про втечу з ешелону і виживання в тайзі.",
                   publisher: "Смолоскип", pages: 304,
                   copies: [("INV-0003", "A-02", CopyCondition.Good)],
                   genres: [(2, true), (4, false)]),

            Create("Місто", "978-966-03-5555-3", 4.25m, authorId: 3, year: 1928,
                   description: "Урбаністичний роман про перетворення селянина на письменника.",
                   publisher: "Основи", pages: 288,
                   copies: [("INV-0004", "B-01", CopyCondition.Worn)],
                   genres: [(3, true), (4, false)]),

            Create("Інтернат", "978-966-03-5555-4", 5.00m, authorId: 4, year: 2017,
                   description: "Роман про три дні з життя вчителя на тлі війни на сході України.",
                   publisher: "Meridian Czernowitz", pages: 336,
                   copies: [("INV-0005", "B-02", CopyCondition.New)],
                   genres: [(3, true)]),

            Create("Чорний Ворон", "978-966-03-5555-5", 4.75m, authorId: 5, year: 2009,
                   description: "Роман про отамана Чорного Ворона та повстанський рух 1920-х.",
                   publisher: "Ярославів Вал", pages: 432,
                   copies: [("INV-0006", "C-01", CopyCondition.Good)],
                   genres: [(2, true), (3, false)]),

            Create("Записки українського самашедшого", "978-966-03-5555-6", 5.50m, authorId: 6, year: 2010,
                   description: "Роман-щоденник про події початку 2000-х в Україні.",
                   publisher: "Либідь", pages: 416,
                   copies: [("INV-0007", "C-02", CopyCondition.Good)],
                   genres: [(5, true)]),
        };

        foreach (var book in books)
        {
            // друга лінія захисту від дублікатів — перевірка за бізнес-ключем
            if (await db.Books.AnyAsync(b => b.Isbn == book.Isbn, ct))
                continue;

            await db.Books.AddAsync(book, ct);
        }

        var saved = await db.SaveChangesAsync(ct);
        logger?.LogInformation("Сідінг завершено: збережено {Count} змін.", saved);
    }

    private static Book Create(
        string title, string isbn, decimal fee, int authorId, int year,
        string description, string publisher, int pages,
        (string Inv, string Shelf, string Condition)[] copies,
        (int GenreId, bool IsPrimary)[] genres)
    {
        var book = new Book(title, isbn, fee, authorId, year);

        book.SetDetail(new BookDetail(description, publisher, pages));

        // AddCopy сам збільшує AvailableCopies — залишок завжди узгоджений
        // із кількістю фізичних примірників
        foreach (var (inv, shelf, condition) in copies)
            book.AddCopy(new BookCopy(inv, shelf, condition));

        foreach (var (genreId, isPrimary) in genres)
            book.AssignGenre(genreId, isPrimary, addedBy: "seed");

        return book;
    }
}
