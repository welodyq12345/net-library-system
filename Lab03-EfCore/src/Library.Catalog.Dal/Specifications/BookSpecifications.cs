using Ardalis.Specification;
using Library.Catalog.Domain.Entities;

namespace Library.Catalog.Dal.Specifications;

/// <summary>
/// Параметри запиту списку книг. Використовуються і в API (biding з query-рядка),
/// і в специфікації.
/// </summary>
public sealed record BookQuery(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    decimal? MinFee = null,
    decimal? MaxFee = null,
    int? AuthorId = null,
    string? Genre = null,
    bool? OnlyAvailable = null,
    string SortBy = "title",
    string SortDir = "asc")
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;

    /// <summary>Розмір сторінки, обмежений зверху й знизу.</summary>
    public int SafePageSize => Math.Clamp(PageSize <= 0 ? DefaultPageSize : PageSize, 1, MaxPageSize);

    public int SafePage => Page < 1 ? 1 : Page;

    /// <summary>Дозволені поля сортування — захист від довільного вводу.</summary>
    public static readonly IReadOnlyCollection<string> AllowedSortFields =
        new[] { "title", "fee", "year", "copies" };
}

/// <summary>
/// Специфікація списку книг: фільтрація, сортування та посторінковість.
/// </summary>
/// <remarks>
/// <para>
/// Специфікація описує КРИТЕРІЇ вибірки окремо від репозиторію й не залежить
/// від EF, тому її можна тестувати ізольовано. Репозиторій лише приймає її як
/// параметр, інтерпретує (<c>WithSpecification</c>) і будує LINQ-вираз.
/// </para>
/// <para>
/// <b>Важливо:</b> усе — <c>Where</c>, <c>OrderBy</c>, <c>Skip</c>/<c>Take</c> —
/// потрапляє в один SQL-запит і виконується на рівні БД (<c>OFFSET … FETCH</c>).
/// Повні таблиці в памʼять не завантажуються.
/// </para>
/// </remarks>
public sealed class BooksFilterSpec : Specification<Book>
{
    public BooksFilterSpec(BookQuery q, bool applyPaging = true)
    {
        Query.Where(b => !b.IsDeleted);

        Query.Include(b => b.Author);
        Query.Include(b => b.BookGenres).ThenInclude(bg => bg.Genre);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var search = q.Search.Trim();
            Query.Where(b => b.Title.Contains(search) || b.Isbn.Contains(search));
        }

        if (q.MinFee is not null)
            Query.Where(b => b.DailyFee >= q.MinFee);

        if (q.MaxFee is not null)
            Query.Where(b => b.DailyFee <= q.MaxFee);

        if (q.AuthorId is not null)
            Query.Where(b => b.AuthorId == q.AuthorId);

        if (!string.IsNullOrWhiteSpace(q.Genre))
        {
            var genre = q.Genre.Trim();
            Query.Where(b => b.BookGenres.Any(bg => bg.Genre.Name == genre || bg.Genre.Slug == genre));
        }

        if (q.OnlyAvailable == true)
            Query.Where(b => b.AvailableCopies > 0);

        // сортування лише за дозволеними полями
        var desc = string.Equals(q.SortDir, "desc", StringComparison.OrdinalIgnoreCase);
        switch (q.SortBy?.ToLowerInvariant())
        {
            case "fee":
                if (desc) Query.OrderByDescending(b => b.DailyFee); else Query.OrderBy(b => b.DailyFee);
                break;
            case "year":
                if (desc) Query.OrderByDescending(b => b.PublishedYear); else Query.OrderBy(b => b.PublishedYear);
                break;
            case "copies":
                if (desc) Query.OrderByDescending(b => b.AvailableCopies); else Query.OrderBy(b => b.AvailableCopies);
                break;
            default:
                if (desc) Query.OrderByDescending(b => b.Title); else Query.OrderBy(b => b.Title);
                break;
        }

        if (applyPaging)
            Query.Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize);

        Query.AsNoTracking();
    }
}

/// <summary>
/// ДРУГА специфікація з іншими критеріями — доводить, що репозиторій
/// працює з будь-якою специфікацією без змін у своєму коді.
/// </summary>
public sealed class BooksByGenreSlugSpec : Specification<Book>
{
    public BooksByGenreSlugSpec(string genreSlug, int take = 10)
    {
        Query.Where(b => !b.IsDeleted && b.BookGenres.Any(bg => bg.Genre.Slug == genreSlug))
             .Include(b => b.Author)
             .OrderByDescending(b => b.AvailableCopies)
             .Take(take)
             .AsNoTracking();
    }
}

/// <summary>Специфікація «книги, яких немає в наявності» — для звіту комплектування.</summary>
public sealed class OutOfStockBooksSpec : Specification<Book>
{
    public OutOfStockBooksSpec()
    {
        Query.Where(b => !b.IsDeleted && b.AvailableCopies == 0)
             .Include(b => b.Author)
             .OrderBy(b => b.Title)
             .AsNoTracking();
    }
}
