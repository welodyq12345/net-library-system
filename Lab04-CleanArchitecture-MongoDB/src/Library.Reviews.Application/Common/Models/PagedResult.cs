namespace Library.Reviews.Application.Common.Models;

/// <summary>
/// Контракт посторінкової відповіді. Той самий, що в Лаб. 03, — клієнти
/// платформи бачать однаковий формат від усіх сервісів.
/// </summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    long TotalCount,
    int Page,
    int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNext => Page < TotalPages;
    public bool HasPrevious => Page > 1;

    public static PagedResult<T> Empty(int page, int pageSize) => new([], 0, page, pageSize);
}

/// <summary>
/// Сторінка курсорної пагінації: наступна порція починається ПІСЛЯ
/// останнього отриманого документа, а не через <c>skip</c>, який
/// сповільнюється зі зростанням номера сторінки.
/// </summary>
public record CursorPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor)
{
    public bool HasMore => NextCursor is not null;
}
