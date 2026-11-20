namespace Library.Platform.Aggregator.Dtos;

/* =============================================================================
   DTO Агрегатора (Частина 6.3).

   ВАЖЛИВО: це ВЛАСНІ контракти Агрегатора, а не посилання на збірки
   мікросервісів. Агрегатор спілкується з ними лише по HTTP, тому спільних
   типів у коді бути не повинно — інакше зникає незалежність сервісів і
   кожна зміна в мікросервісі ламала б збірку Агрегатора.

   Описані лише ті поля, які потрібні для бізнес-сценарію; решту
   System.Text.Json просто проігнорує.
   ============================================================================= */

// ----- те, що віддає Сервіс видач (Лаб. 02) -------------------------------

public record LoanItemDto(
    long BookId,
    string BookTitle,
    string BookAuthor,
    decimal DailyFee,
    int Units,
    decimal DailyCharge);

public record LoanDto(
    long Id,
    long ReaderId,
    string Status,
    DateTime LoanDate,
    DateOnly DueDate,
    DateTime? IssuedAt,
    DateTime? ReturnedAt,
    decimal FineAmount,
    IReadOnlyList<LoanItemDto> Items);

public record ReaderDto(
    long Id,
    string CardNumber,
    string FullName,
    string Email,
    bool IsBlocked);

// ----- те, що віддає Каталог (Лаб. 03) ------------------------------------

public record BookDto(
    long Id,
    string Title,
    string Isbn,
    decimal DailyFee,
    int AvailableCopies,
    int? PublishedYear,
    string AuthorName,
    IReadOnlyList<string> Genres);

// ----- те, що віддає Сервіс відгуків (Лаб. 04) ----------------------------

public record ReviewAuthorDto(long? ReaderId, string DisplayName);

public record ReviewDto(
    string Id,
    long BookId,
    string BookTitle,
    ReviewAuthorDto Author,
    int Rating,
    string? Text,
    int CommentsCount,
    DateTime CreatedAt);

public record BookRatingDto(
    long BookId,
    string BookTitle,
    long ReviewsCount,
    double AverageRating,
    int MinRating,
    int MaxRating);

/// <summary>Сторінка результатів — спільний формат списків у Лаб. 03–04.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);

// ----- КОМПОЗИТНІ DTO самого Агрегатора -----------------------------------

/// <summary>
/// Скільки часу зайняв кожен виклик і чим він завершився.
/// </summary>
/// <remarks>
/// Поле існує, щоб паралельність було ВИДНО: сума тривалостей викликів
/// помітно більша за <c>TotalMs</c> усієї агрегації.
/// </remarks>
public record SourceCallDto(string Service, string Status, long ElapsedMs);

public record AggregationMetaDto(
    long TotalMs,
    long SequentialMs,
    IReadOnlyList<SourceCallDto> Calls)
{
    /// <summary>Скільки часу зекономила паралельність.</summary>
    public long SavedByParallelismMs => Math.Max(0, SequentialMs - TotalMs);
}

/// <summary>
/// Повна картка видачі — бізнес-сценарій «усе про цю видачу на одному екрані».
/// </summary>
/// <remarks>
/// Структура описує САМ СЦЕНАРІЙ, а не копіює структури окремих сервісів:
/// читач, книги з актуальними даними Каталогу, свіжі відгуки на ці книги
/// та їхні зведені рейтинги в одній відповіді.
/// </remarks>
public record FullLoanDto(
    LoanDto Loan,
    ReaderDto? Reader,
    IReadOnlyList<BookDto> Books,
    IReadOnlyList<ReviewDto> Reviews,
    IReadOnlyList<BookRatingDto> Ratings,
    IReadOnlyList<string> Warnings,
    AggregationMetaDto Meta);

/// <summary>Локальна проєкція довідника книг у Сервісі видач (Лаб. 02).</summary>
/// <remarks>
/// Денормалізована копія: Сервіс видач тримає власний довідник, щоб не
/// звертатися до Каталогу на кожну операцію. <c>SyncedAt</c> показує, коли
/// копію синхронізували останній раз, тому картка книги дає побачити
/// розходження між канонічними даними Каталогу та копією.
/// </remarks>
public record LendingBookDto(
    long Id,
    string Title,
    string Author,
    decimal DailyFee,
    int CopiesAvailable,
    DateTime SyncedAt);

/// <summary>Картка книги: дані Каталогу + копія Сервісу видач + відгуки й рейтинг.</summary>
public record BookCardDto(
    BookDto Book,
    LendingBookDto? LendingProjection,
    BookRatingDto? Rating,
    IReadOnlyList<ReviewDto> Reviews,
    IReadOnlyList<string> Warnings,
    AggregationMetaDto Meta);
