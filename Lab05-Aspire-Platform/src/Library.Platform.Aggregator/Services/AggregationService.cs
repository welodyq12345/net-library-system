using System.Diagnostics;
using Polly;
using Library.Platform.Aggregator.Clients;
using Library.Platform.Aggregator.Dtos;

namespace Library.Platform.Aggregator.Services;

/* =============================================================================
   Aggregator Pattern (Частина 6.2).

   Клієнтові потрібна одна відповідь, а дані лежать у трьох незалежних
   сервісах із власними базами. Агрегатор опитує їх ПАРАЛЕЛЬНО
   (Task.WhenAll) і збирає результат:

       три виклики по ~100 мс паралельно ≈ 100 мс, послідовно — 300 мс.

   Часткова відмова НЕ «валить» запит: недоступність Каталогу чи Відгуків
   додає рядок у Warnings, а клієнт отримує те, що вдалося зібрати.
   Виняток — сам об'єкт, навколо якого будується відповідь (видача): без
   нього агрегувати нічого, тому повертається 404.
   ============================================================================= */

public interface IAggregationService
{
    Task<FullLoanDto?> GetFullLoanAsync(long loanId, CancellationToken ct);
    Task<BookCardDto?> GetBookCardAsync(long bookId, CancellationToken ct);
}

public sealed class AggregationService(
    ILendingClient lending,
    ICatalogClient catalog,
    IReviewsClient reviews,
    ILogger<AggregationService> logger) : IAggregationService
{
    /// <summary>
    /// Джерело власних spans Агрегатора.
    /// </summary>
    /// <remarks>
    /// Ім'я збігається з тим, що ServiceDefaults передає в
    /// <c>AddSource(builder.Environment.ApplicationName)</c>, тому ці spans
    /// з'являються в Aspire Dashboard поряд з автоматичними spans HTTP і БД.
    /// </remarks>
    private static readonly ActivitySource Source = new("Library.Platform.Aggregator");

    private const int ReviewsPerBook = 5;

    /// <summary>Повна картка видачі: видача + читач + книги + відгуки + рейтинги.</summary>
    public async Task<FullLoanDto?> GetFullLoanAsync(long loanId, CancellationToken ct)
    {
        using var activity = Source.StartActivity("AggregateFullLoan");
        activity?.SetTag("loan.id", loanId);

        var warnings = new List<string>();
        var calls = new List<SourceCallDto>();
        var total = Stopwatch.StartNew();

        // 1. Видача — послідовно: без неї невідомо, які книги й якого читача питати
        var loan = await TrackAsync(
            () => lending.GetLoanAsync(loanId, ct), "lending-api/loans", warnings, calls);

        if (loan is null)
        {
            logger.LogInformation("Видачу {LoanId} не знайдено — агрегувати нічого", loanId);
            return null;
        }

        var bookIds = loan.Items.Select(i => i.BookId).Distinct().ToList();
        activity?.SetTag("loan.books", bookIds.Count);
        activity?.SetTag("reader.id", loan.ReaderId);

        logger.LogInformation(
            "Агрегую видачу {LoanId}: читач {ReaderId}, книг {BooksCount}",
            loanId, loan.ReaderId, bookIds.Count);

        // 2. Решта — ПАРАЛЕЛЬНО. Токен скасування передається в усі виклики:
        //    якщо клієнт обірвав запит, вихідні виклики теж зупиняються.
        var readerTask = TrackAsync(
            () => lending.GetReaderAsync(loan.ReaderId, ct), "lending-api/readers", warnings, calls);

        var booksTask = TrackAsync(
            () => catalog.GetBooksAsync(bookIds, ct), "catalog-api", warnings, calls);

        var reviewsTask = TrackAsync(
            () => reviews.GetForBooksAsync(bookIds, ReviewsPerBook, ct), "reviews-api/reviews", warnings, calls);

        var ratingsTask = TrackAsync(
            () => reviews.GetRatingsAsync(bookIds, ct), "reviews-api/ratings", warnings, calls);

        // НЕ Task.WaitAll і не .Result — вони блокують потік пулу
        await Task.WhenAll(readerTask, booksTask, reviewsTask, ratingsTask);

        total.Stop();

        // 3. Перевірка узгодженості: у відповіді лише книги ЦІЄЇ видачі
        var books = (await booksTask ?? []).Where(b => bookIds.Contains(b.Id)).ToList();
        var bookReviews = (await reviewsTask ?? []).Where(r => bookIds.Contains(r.BookId)).ToList();
        var ratings = (await ratingsTask ?? []).Where(r => bookIds.Contains(r.BookId)).ToList();

        if (books.Count < bookIds.Count && !warnings.Any(w => w.Contains("catalog-api")))
            warnings.Add($"catalog-api: знайдено {books.Count} книг із {bookIds.Count}");

        var meta = BuildMeta(total.ElapsedMilliseconds, calls);

        activity?.SetTag("aggregation.warnings", warnings.Count);
        activity?.SetTag("aggregation.total_ms", meta.TotalMs);

        logger.LogInformation(
            "Видачу {LoanId} агреговано за {TotalMs} мс (послідовно було б {SequentialMs} мс), "
            + "попереджень: {WarningsCount}",
            loanId, meta.TotalMs, meta.SequentialMs, warnings.Count);

        return new FullLoanDto(loan, await readerTask, books, bookReviews, ratings, warnings, meta);
    }

    /// <summary>Картка книги: Каталог + копія Сервісу видач + відгуки й рейтинг.</summary>
    public async Task<BookCardDto?> GetBookCardAsync(long bookId, CancellationToken ct)
    {
        using var activity = Source.StartActivity("AggregateBookCard");
        activity?.SetTag("book.id", bookId);

        var warnings = new List<string>();
        var calls = new List<SourceCallDto>();
        var total = Stopwatch.StartNew();

        long[] ids = [bookId];

        // Каталог — канонічне джерело даних про книгу, тому він у критичному шляху.
        // Решта виконується одночасно з ним: якщо книги немає, зайві результати
        // просто відкидаються — це дешевше за додатковий послідовний крок.
        var bookTask = TrackAsync(
            () => catalog.GetBookAsync(bookId, ct), "catalog-api", warnings, calls);

        var projectionTask = TrackAsync(
            () => lending.GetBookProjectionAsync(bookId, ct), "lending-api/books", warnings, calls);

        var reviewsTask = TrackAsync(
            () => reviews.GetForBookAsync(bookId, ReviewsPerBook, ct), "reviews-api/reviews", warnings, calls);

        var ratingsTask = TrackAsync(
            () => reviews.GetRatingsAsync(ids, ct), "reviews-api/ratings", warnings, calls);

        await Task.WhenAll(bookTask, projectionTask, reviewsTask, ratingsTask);

        total.Stop();

        var book = await bookTask;
        if (book is null)
        {
            logger.LogInformation("Книгу {BookId} у Каталозі не знайдено", bookId);
            return null;
        }

        var rating = (await ratingsTask ?? []).FirstOrDefault(r => r.BookId == bookId);
        var meta = BuildMeta(total.ElapsedMilliseconds, calls);

        logger.LogInformation(
            "Картку книги {BookId} зібрано за {TotalMs} мс, попереджень: {WarningsCount}",
            bookId, meta.TotalMs, warnings.Count);

        return new BookCardDto(
            book, await projectionTask, rating,
            (await reviewsTask ?? []).Where(r => r.BookId == bookId).ToList(),
            warnings, meta);
    }

    /// <summary>
    /// Виконує виклик сервісу, міряє його тривалість і ПОГЛИНАЄ помилки
    /// доступності, перетворюючи їх на попередження.
    /// </summary>
    /// <remarks>
    /// Перехоплюються лише помилки ДОСТУПНОСТІ сервісу:
    /// <list type="bullet">
    ///   <item><see cref="HttpRequestException"/> — з'єднання не встановлено;</item>
    ///   <item><see cref="TaskCanceledException"/> — запит не встиг;</item>
    ///   <item><see cref="ExecutionRejectedException"/> — виклик відхилив сам
    ///   конвеєр стійкості: вичерпано загальний бюджет часу
    ///   (<c>TimeoutRejectedException</c>) або розімкнувся запобіжник
    ///   (<c>BrokenCircuitException</c>). Без цієї гілки зупинений сервіс давав
    ///   би 500 замість часткової відповіді.</item>
    /// </list>
    /// Помилки в самому Агрегаторі маскувати не можна — вони мають дійти до
    /// глобального обробника.
    /// </remarks>
    private async Task<T?> TrackAsync<T>(
        Func<Task<T>> call, string service, List<string> warnings, List<SourceCallDto> calls)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            logger.LogDebug("Викликаю {Service}", service);

            var result = await call();
            stopwatch.Stop();

            logger.LogInformation("{Service} відповів за {Elapsed} мс",
                service, stopwatch.ElapsedMilliseconds);

            // списки спільні для паралельних задач -> доступ лише під блокуванням
            lock (calls)
                calls.Add(new SourceCallDto(service, "ok", stopwatch.ElapsedMilliseconds));

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                      or TaskCanceledException
                                      or ExecutionRejectedException)
        {
            stopwatch.Stop();

            logger.LogWarning(ex,
                "{Service} недоступний ({Reason}) — відповідь буде часткова",
                service, ex.GetType().Name);

            lock (warnings)
                warnings.Add($"{service} is unavailable: {ex.Message}");

            lock (calls)
                calls.Add(new SourceCallDto(service, "unavailable", stopwatch.ElapsedMilliseconds));

            return default;
        }
    }

    private static AggregationMetaDto BuildMeta(long totalMs, List<SourceCallDto> calls)
    {
        // послідовне виконання тривало б як сума всіх викликів —
        // саме цим числом видно виграш від Task.WhenAll
        var ordered = calls.OrderBy(c => c.Service).ToList();
        return new AggregationMetaDto(totalMs, ordered.Sum(c => c.ElapsedMs), ordered);
    }
}
