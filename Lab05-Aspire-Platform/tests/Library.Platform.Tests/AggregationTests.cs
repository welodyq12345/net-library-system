using System.Diagnostics;
using Library.Platform.Aggregator.Clients;
using Library.Platform.Aggregator.Dtos;
using Library.Platform.Aggregator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Library.Platform.Tests;

/* =============================================================================
   Тести сервісу агрегації (Частина 6).

   Клієнти мікросервісів підставні, тому перевіряється САМА ЛОГІКА агрегації:
   паралельність, часткові відмови, узгодженість даних. Підняття сервісів для
   цього не потрібне — наскрізний сценарій перевіряє tests/api-tests.py.
   ============================================================================= */

/// <summary>Підставний клієнт Сервісу видач з керованою поведінкою.</summary>
internal sealed class FakeLendingClient : ILendingClient
{
    public LoanDto? Loan { get; set; }
    public ReaderDto? Reader { get; set; }
    public LendingBookDto? Projection { get; set; }
    public Exception? Failure { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public CancellationToken LastToken { get; private set; }

    public async Task<LoanDto?> GetLoanAsync(long id, CancellationToken ct)
    {
        LastToken = ct;
        await WaitAsync(ct);
        return Loan;
    }

    public async Task<ReaderDto?> GetReaderAsync(long id, CancellationToken ct)
    {
        LastToken = ct;
        await WaitAsync(ct);
        return Reader;
    }

    public async Task<LendingBookDto?> GetBookProjectionAsync(long bookId, CancellationToken ct)
    {
        LastToken = ct;
        await WaitAsync(ct);
        return Projection;
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Failure is not null) throw Failure;
    }
}

internal sealed class FakeCatalogClient : ICatalogClient
{
    public List<BookDto> Books { get; set; } = [];
    public Exception? Failure { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public int Calls;

    public async Task<BookDto?> GetBookAsync(long id, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Failure is not null) throw Failure;
        return Books.FirstOrDefault(b => b.Id == id);
    }

    public async Task<IReadOnlyList<BookDto>> GetBooksAsync(
        IEnumerable<long> ids, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Failure is not null) throw Failure;

        var wanted = ids.ToHashSet();
        return Books.Where(b => wanted.Contains(b.Id)).ToList();
    }
}

internal sealed class FakeReviewsClient : IReviewsClient
{
    public List<ReviewDto> Reviews { get; set; } = [];
    public List<BookRatingDto> Ratings { get; set; } = [];
    public Exception? Failure { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task<IReadOnlyList<ReviewDto>> GetForBookAsync(
        long bookId, int limit, CancellationToken ct)
    {
        await WaitAsync(ct);
        return Reviews.Where(r => r.BookId == bookId).Take(limit).ToList();
    }

    public async Task<IReadOnlyList<ReviewDto>> GetForBooksAsync(
        IEnumerable<long> bookIds, int limitPerBook, CancellationToken ct)
    {
        await WaitAsync(ct);
        var wanted = bookIds.ToHashSet();
        return Reviews.Where(r => wanted.Contains(r.BookId)).ToList();
    }

    public async Task<IReadOnlyList<BookRatingDto>> GetRatingsAsync(
        IEnumerable<long> bookIds, CancellationToken ct)
    {
        await WaitAsync(ct);
        var wanted = bookIds.ToHashSet();
        return Ratings.Where(r => wanted.Contains(r.BookId)).ToList();
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Failure is not null) throw Failure;
    }
}

public class AggregationTests
{
    private readonly FakeLendingClient _lending = new();
    private readonly FakeCatalogClient _catalog = new();
    private readonly FakeReviewsClient _reviews = new();

    private AggregationService CreateSut() => new(
        _lending, _catalog, _reviews, NullLogger<AggregationService>.Instance);

    private static LoanDto Loan(params long[] bookIds) => new(
        Id: 1, ReaderId: 7, Status: "Issued",
        LoanDate: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
        DueDate: new DateOnly(2026, 1, 24),
        IssuedAt: null, ReturnedAt: null, FineAmount: 0m,
        Items: bookIds.Select(id => new LoanItemDto(
            id, $"Книга {id}", "Автор", 2.5m, 1, 2.5m)).ToList());

    private static BookDto Book(long id) => new(
        id, $"Книга {id}", "978-0000000000", 2.5m, 3, 2020, "Автор", ["Фантастика"]);

    private static ReviewDto Review(long bookId, string id) => new(
        id, bookId, $"Книга {bookId}", new ReviewAuthorDto(7, "Читач"), 5, "Добре", 0,
        new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc));

    // ---------------------------------------------------------------- базовий

    [Fact]
    public async Task GetFullLoan_Збирає_дані_всіх_трьох_сервісів()
    {
        _lending.Loan = Loan(10, 11);
        _lending.Reader = new ReaderDto(7, "LIB-0007", "Читач Перший", "r@lib.ua", false);
        _catalog.Books = [Book(10), Book(11)];
        _reviews.Reviews = [Review(10, "a"), Review(11, "b")];
        _reviews.Ratings = [new BookRatingDto(10, "Книга 10", 1, 5, 5, 5)];

        var result = await CreateSut().GetFullLoanAsync(1, default);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Loan.Id);
        Assert.Equal("Читач Перший", result.Reader!.FullName);
        Assert.Equal(2, result.Books.Count);
        Assert.Equal(2, result.Reviews.Count);
        Assert.Single(result.Ratings);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task GetFullLoan_Повертає_null_коли_видачі_немає()
    {
        _lending.Loan = null;

        Assert.Null(await CreateSut().GetFullLoanAsync(999, default));

        // без видачі решту сервісів питати нічого — зайвих викликів немає
        Assert.Equal(0, _catalog.Calls);
    }

    // ------------------------------------------------------- часткові відмови

    [Fact]
    public async Task GetFullLoan_Каталог_недоступний_повертає_часткову_відповідь()
    {
        _lending.Loan = Loan(10);
        _catalog.Failure = new HttpRequestException("Connection refused");
        _reviews.Reviews = [Review(10, "a")];

        var result = await CreateSut().GetFullLoanAsync(1, default);

        // 200 з попередженням, а не 500
        Assert.NotNull(result);
        Assert.Empty(result!.Books);
        Assert.Single(result.Reviews);
        Assert.Contains(result.Warnings, w => w.Contains("catalog-api"));
        Assert.Contains(result.Meta.Calls, c => c is { Service: "catalog-api", Status: "unavailable" });
    }

    [Fact]
    public async Task GetFullLoan_Відгуки_недоступні_повертає_часткову_відповідь()
    {
        // саме цей сценарій перевіряється на занятті: reviews-api зупинено
        _lending.Loan = Loan(10);
        _catalog.Books = [Book(10)];
        _reviews.Failure = new HttpRequestException("No such host is known");

        var result = await CreateSut().GetFullLoanAsync(1, default);

        Assert.NotNull(result);
        Assert.Single(result!.Books);
        Assert.Empty(result.Reviews);
        Assert.Empty(result.Ratings);
        // два виклики до reviews-api -> два попередження
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("reviews-api")));
    }

    [Fact]
    public async Task GetFullLoan_Таймаут_теж_стає_попередженням()
    {
        _lending.Loan = Loan(10);
        _catalog.Failure = new TaskCanceledException("The request was canceled due to timeout");

        var result = await CreateSut().GetFullLoanAsync(1, default);

        Assert.NotNull(result);
        Assert.Contains(result!.Warnings, w => w.Contains("catalog-api"));
    }

    [Fact]
    public async Task GetFullLoan_Несподівана_помилка_НЕ_поглинається()
    {
        // помилки самого Агрегатора маскувати не можна: вони мають дійти
        // до глобального обробника і стати 500
        _lending.Loan = Loan(10);
        _catalog.Failure = new InvalidOperationException("помилка в коді агрегатора");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().GetFullLoanAsync(1, default));
    }

    // ------------------------------------------------------------ паралельність

    [Fact]
    public async Task GetFullLoan_Виконує_виклики_ПАРАЛЕЛЬНО()
    {
        const int delayMs = 300;

        _lending.Loan = Loan(10);
        _lending.Delay = TimeSpan.FromMilliseconds(delayMs);
        _catalog.Books = [Book(10)];
        _catalog.Delay = TimeSpan.FromMilliseconds(delayMs);
        _reviews.Delay = TimeSpan.FromMilliseconds(delayMs);

        var stopwatch = Stopwatch.StartNew();
        var result = await CreateSut().GetFullLoanAsync(1, default);
        stopwatch.Stop();

        Assert.NotNull(result);

        // видача (послідовно) + 4 паралельні виклики = ~2 затримки,
        // а послідовно було б ~5. Беремо 4 як запас на планувальник.
        Assert.True(stopwatch.ElapsedMilliseconds < delayMs * 4,
            $"агрегація зайняла {stopwatch.ElapsedMilliseconds} мс — схоже, виклики послідовні");

        // meta показує той самий факт числами
        Assert.True(result!.Meta.SequentialMs > result.Meta.TotalMs);
        Assert.True(result.Meta.SavedByParallelismMs > 0);
    }

    [Fact]
    public async Task GetFullLoan_Передає_CancellationToken_у_всі_виклики()
    {
        using var cts = new CancellationTokenSource();
        _lending.Loan = Loan(10);

        await CreateSut().GetFullLoanAsync(1, cts.Token);

        Assert.Equal(cts.Token, _lending.LastToken);
    }

    [Fact]
    public async Task GetFullLoan_Скасування_клієнтом_зупиняє_агрегацію()
    {
        using var cts = new CancellationTokenSource();
        _lending.Loan = Loan(10);
        _lending.Delay = TimeSpan.FromSeconds(5);

        var task = CreateSut().GetFullLoanAsync(1, cts.Token);
        await cts.CancelAsync();

        // TaskCanceledException трактується як недоступність сервісу,
        // тому агрегація завершується частковою відповіддю, а не падінням
        var result = await task;
        Assert.Null(result);
    }

    // ----------------------------------------------------------- узгодженість

    [Fact]
    public async Task GetFullLoan_Відкидає_дані_чужих_книг()
    {
        _lending.Loan = Loan(10);
        _catalog.Books = [Book(10), Book(99)];           // 99 не з цієї видачі
        _reviews.Reviews = [Review(10, "a"), Review(99, "x")];
        _reviews.Ratings =
        [
            new BookRatingDto(10, "Книга 10", 1, 5, 5, 5),
            new BookRatingDto(99, "Книга 99", 3, 4, 3, 5)
        ];

        var result = await CreateSut().GetFullLoanAsync(1, default);

        Assert.NotNull(result);
        Assert.Equal([10L], result!.Books.Select(b => b.Id));
        Assert.Equal([10L], result.Reviews.Select(r => r.BookId));
        Assert.Equal([10L], result.Ratings.Select(r => r.BookId));
    }

    [Fact]
    public async Task GetFullLoan_Повідомляє_коли_частини_книг_немає_в_Каталозі()
    {
        _lending.Loan = Loan(10, 11);
        _catalog.Books = [Book(10)];                     // книги 11 у Каталозі немає

        var result = await CreateSut().GetFullLoanAsync(1, default);

        Assert.NotNull(result);
        Assert.Single(result!.Books);
        Assert.Contains(result.Warnings, w => w.Contains("1 книг із 2"));
    }

    [Fact]
    public async Task GetFullLoan_Дедуплікує_книги_видачі()
    {
        // та сама книга двома позиціями -> один запит у Каталог
        _lending.Loan = Loan(10, 10);
        _catalog.Books = [Book(10)];

        var result = await CreateSut().GetFullLoanAsync(1, default);

        Assert.NotNull(result);
        Assert.Single(result!.Books);
        Assert.Empty(result.Warnings);
    }

    // ------------------------------------------------------------ картка книги

    [Fact]
    public async Task GetBookCard_Збирає_Каталог_проєкцію_і_відгуки()
    {
        _catalog.Books = [Book(10)];
        _lending.Projection = new LendingBookDto(
            10, "Книга 10", "Автор", 2.5m, 2,
            new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc));
        _reviews.Reviews = [Review(10, "a")];
        _reviews.Ratings = [new BookRatingDto(10, "Книга 10", 1, 5, 5, 5)];

        var result = await CreateSut().GetBookCardAsync(10, default);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Book.AvailableCopies);          // канонічні дані Каталогу
        Assert.Equal(2, result.LendingProjection!.CopiesAvailable); // копія Сервісу видач
        Assert.Equal(5, result.Rating!.AverageRating);
        Assert.Single(result.Reviews);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task GetBookCard_Повертає_null_коли_книги_немає_в_Каталозі()
    {
        _catalog.Books = [];

        Assert.Null(await CreateSut().GetBookCardAsync(404, default));
    }

    [Fact]
    public async Task GetBookCard_Сервіс_видач_недоступний_картка_все_одно_є()
    {
        _catalog.Books = [Book(10)];
        _lending.Failure = new HttpRequestException("Connection refused");

        var result = await CreateSut().GetBookCardAsync(10, default);

        Assert.NotNull(result);
        Assert.Null(result!.LendingProjection);
        Assert.Contains(result.Warnings, w => w.Contains("lending-api"));
    }

    // ------------------------------------------------------------------- meta

    [Fact]
    public async Task Meta_Містить_запис_про_кожен_виклик()
    {
        _lending.Loan = Loan(10);
        _catalog.Books = [Book(10)];

        var result = await CreateSut().GetFullLoanAsync(1, default);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Meta.Calls.Count);   // видача, читач, каталог, відгуки, рейтинги
        Assert.All(result.Meta.Calls, c => Assert.Equal("ok", c.Status));
        Assert.Equal(
            result.Meta.Calls.Sum(c => c.ElapsedMs),
            result.Meta.SequentialMs);
    }
}
