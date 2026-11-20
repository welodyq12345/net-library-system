using Library.Reviews.Application.Common.Models;

namespace Library.Reviews.Application.Common.Interfaces;

/* =============================================================================
   DTO читання. Лежать в Application, бо є частиною контракту сценаріїв
   використання, а не деталлю зберігання.
   ============================================================================= */

public record AuthorDto(long? ReaderId, string DisplayName);

public record CommentDto(string AuthorName, string Text, DateTime CreatedAt, int Likes);

/// <summary>Коротке представлення відгуку для списків.</summary>
public record ReviewDto(
    string Id,
    long BookId,
    string BookTitle,
    AuthorDto Author,
    int Rating,
    string? Text,
    int CommentsCount,
    string? DiscussionId,
    DateTime CreatedAt,
    int Version);

/// <summary>Повне представлення відгуку з вбудованими коментарями.</summary>
public record ReviewDetailsDto(
    string Id,
    long BookId,
    string BookTitle,
    AuthorDto Author,
    int Rating,
    string? Text,
    IReadOnlyList<CommentDto> Comments,
    IReadOnlyList<string> Photos,
    IReadOnlyList<string> Tags,
    string? DiscussionId,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int Version);

/// <summary>Агрегат: середня оцінка та розподіл оцінок книги.</summary>
public record BookRatingDto(
    long BookId,
    string BookTitle,
    long ReviewsCount,
    double AverageRating,
    int MinRating,
    int MaxRating,
    long CommentsTotal);

/// <summary>
/// Сторона ЧИТАННЯ в CQRS.
/// </summary>
/// <remarks>
/// Обробники запитів з Application звертаються лише до цього інтерфейсу й
/// нічого не знають про MongoDB. Реалізація в Infrastructure використовує
/// проєкції та aggregation pipeline — із бази вибираються тільки потрібні поля.
/// Так само в eShop запити до замовлень оголошено інтерфейсом
/// <c>IOrderQueries</c>.
/// </remarks>
public interface IReviewQueries
{
    Task<ReviewDetailsDto?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Список із пагінацією та фільтрацією засобами MongoDB (skip/limit, $match).</summary>
    Task<PagedResult<ReviewDto>> GetListAsync(
        long? bookId, int? minRating, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Курсорна пагінація — ефективніша за skip на великих наборах.</summary>
    Task<CursorPage<ReviewDto>> GetPageByCursorAsync(
        long? bookId, string? cursor, int pageSize, CancellationToken ct = default);

    /// <summary>Рейтинги книг — aggregation pipeline ($group).</summary>
    Task<IReadOnlyList<BookRatingDto>> GetBookRatingsAsync(CancellationToken ct = default);

    /// <summary>Повнотекстовий пошук через text-індекс.</summary>
    Task<IReadOnlyList<ReviewDto>> SearchAsync(string term, int limit, CancellationToken ct = default);
}
