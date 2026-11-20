using Library.Platform.Aggregator.Dtos;

namespace Library.Platform.Aggregator.Clients;

/* =============================================================================
   Typed HttpClients (Частина 6.1) — по одному класу на мікросервіс.

   BaseAddress задається в Program.cs як "https+http://lending-api":
     • lending-api — ЛОГІЧНЕ ім'я ресурсу в AppHost, не хост і не порт;
     • схема https+http означає «HTTPS, якщо сервіс його має, інакше HTTP»;
     • реальну адресу підставляє service discovery Aspire.

   CorrelationId у вихідні запити додає CorrelationIdHandler, підключений до
   ВСІХ HttpClient-ів у ServiceDefaults — у клієнтах про нього нічого не знають.

   404 трактується як «немає даних» (null), а не як помилка: інакше відсутня
   книга в Каталозі «валила» б усю агрегацію.
   ============================================================================= */

/* -----------------------------------------------------------------------------
   Інтерфейси клієнтів існують заради ТЕСТІВ: сервіс агрегації залежить від
   абстракції, тому часткову відмову («сервіс недоступний») у модульному тесті
   можна відтворити підставним клієнтом, без підняття мікросервісів.
   ----------------------------------------------------------------------------- */

public interface ILendingClient
{
    Task<LoanDto?> GetLoanAsync(long id, CancellationToken ct);
    Task<ReaderDto?> GetReaderAsync(long id, CancellationToken ct);
    Task<LendingBookDto?> GetBookProjectionAsync(long bookId, CancellationToken ct);
}

public interface ICatalogClient
{
    Task<BookDto?> GetBookAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<BookDto>> GetBooksAsync(IEnumerable<long> ids, CancellationToken ct);
}

public interface IReviewsClient
{
    Task<IReadOnlyList<ReviewDto>> GetForBookAsync(long bookId, int limit, CancellationToken ct);
    Task<IReadOnlyList<ReviewDto>> GetForBooksAsync(
        IEnumerable<long> bookIds, int limitPerBook, CancellationToken ct);
    Task<IReadOnlyList<BookRatingDto>> GetRatingsAsync(
        IEnumerable<long> bookIds, CancellationToken ct);
}

/// <summary>Базові дії, спільні для всіх типізованих клієнтів.</summary>
public abstract class ServiceClientBase(HttpClient http, ILogger logger)
{
    protected HttpClient Http { get; } = http;
    protected ILogger Logger { get; } = logger;

    /// <summary>GET, у якому 404 повертає <c>null</c>, а не виняток.</summary>
    protected async Task<T?> GetOrDefaultAsync<T>(string url, CancellationToken ct)
    {
        Logger.LogDebug("GET {Service}{Url}", Http.BaseAddress, url);

        using var response = await Http.GetAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Logger.LogDebug("{Url}: 404 — даних немає", url);
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
}

/// <summary>Клієнт Сервісу видач (Лаб. 02: SQL Server + ADO.NET/Dapper).</summary>
public sealed class LendingClient(HttpClient http, ILogger<LendingClient> logger)
    : ServiceClientBase(http, logger), ILendingClient
{
    public Task<LoanDto?> GetLoanAsync(long id, CancellationToken ct)
        => GetOrDefaultAsync<LoanDto>($"/api/loans/{id}", ct);

    public Task<ReaderDto?> GetReaderAsync(long id, CancellationToken ct)
        => GetOrDefaultAsync<ReaderDto>($"/api/readers/{id}", ct);

    public Task<LendingBookDto?> GetBookProjectionAsync(long bookId, CancellationToken ct)
        => GetOrDefaultAsync<LendingBookDto>($"/api/books/{bookId}", ct);
}

/// <summary>Клієнт Каталогу (Лаб. 03: SQL Server + EF Core).</summary>
public sealed class CatalogClient(HttpClient http, ILogger<CatalogClient> logger)
    : ServiceClientBase(http, logger), ICatalogClient
{
    public Task<BookDto?> GetBookAsync(long id, CancellationToken ct)
        => GetOrDefaultAsync<BookDto>($"/api/books/{id}", ct);

    /// <summary>
    /// Книги за переліком ідентифікаторів.
    /// </summary>
    /// <remarks>
    /// Каталог не має ендпоінта «кілька книг за списком id», тому клієнт
    /// робить кілька запитів ОДНОЧАСНО. Відсутні книги (404) відкидаються.
    /// </remarks>
    public async Task<IReadOnlyList<BookDto>> GetBooksAsync(
        IEnumerable<long> ids, CancellationToken ct)
    {
        var books = await Task.WhenAll(ids.Select(id => GetBookAsync(id, ct)));

        return books.Where(b => b is not null).Select(b => b!).ToList();
    }
}

/// <summary>Клієнт Сервісу відгуків (Лаб. 04: MongoDB + Clean Architecture).</summary>
public sealed class ReviewsClient(HttpClient http, ILogger<ReviewsClient> logger)
    : ServiceClientBase(http, logger), IReviewsClient
{
    /// <summary>Останні відгуки на книгу.</summary>
    public async Task<IReadOnlyList<ReviewDto>> GetForBookAsync(
        long bookId, int limit, CancellationToken ct)
    {
        var page = await GetOrDefaultAsync<PagedResult<ReviewDto>>(
            $"/api/reviews?bookId={bookId}&page=1&pageSize={limit}", ct);

        return page?.Items ?? [];
    }

    /// <summary>Відгуки на кілька книг — запити виконуються одночасно.</summary>
    public async Task<IReadOnlyList<ReviewDto>> GetForBooksAsync(
        IEnumerable<long> bookIds, int limitPerBook, CancellationToken ct)
    {
        var batches = await Task.WhenAll(
            bookIds.Select(id => GetForBookAsync(id, limitPerBook, ct)));

        return batches.SelectMany(b => b).ToList();
    }

    /// <summary>
    /// Зведені рейтинги книг — на боці MongoDB це aggregation pipeline (Лаб. 04).
    /// </summary>
    public async Task<IReadOnlyList<BookRatingDto>> GetRatingsAsync(
        IEnumerable<long> bookIds, CancellationToken ct)
    {
        var all = await GetOrDefaultAsync<List<BookRatingDto>>("/api/reviews/ratings", ct)
                  ?? [];

        var wanted = bookIds.ToHashSet();
        return all.Where(r => wanted.Contains(r.BookId)).ToList();
    }
}
