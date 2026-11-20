using Library.Catalog.Bll.Dtos;
using Library.Catalog.Dal.Specifications;

namespace Library.Catalog.Bll.Abstractions;

/// <summary>Бізнес-операції над книгами каталогу.</summary>
public interface IBookService
{
    /// <summary>Список книг із пагінацією, фільтрацією та сортуванням на рівні БД.</summary>
    Task<PagedResult<BookListItemDto>> GetListAsync(BookQuery query, CancellationToken ct = default);

    /// <summary>Картка книги — Eager Loading (один SQL-запит).</summary>
    Task<BookDto> GetByIdAsync(long id, CancellationToken ct = default);

    Task<BookDto> CreateAsync(CreateBookDto input, CancellationToken ct = default);

    Task<BookDto> UpdateAsync(long id, UpdateBookDto input, CancellationToken ct = default);

    /// <summary>Списання примірників. Інваріант перевіряє сама сутність.</summary>
    Task<BookDto> RemoveStockAsync(long id, ChangeStockDto input, CancellationToken ct = default);

    Task<BookDto> AddStockAsync(long id, ChangeStockDto input, CancellationToken ct = default);

    Task<BookDto> AddCopyAsync(long id, AddCopyDto input, CancellationToken ct = default);

    Task<BookDto> AssignGenreAsync(long id, AssignGenreDto input, CancellationToken ct = default);

    /// <summary>М'яке видалення книги.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Книги, яких немає в наявності (друга специфікація).</summary>
    Task<IReadOnlyList<BookListItemDto>> GetOutOfStockAsync(CancellationToken ct = default);

    /// <summary>Топ книг жанру за slug (третя специфікація).</summary>
    Task<IReadOnlyList<BookListItemDto>> GetTopByGenreSlugAsync(
        string slug, int take, CancellationToken ct = default);
}

/// <summary>Бізнес-операції над авторами.</summary>
public interface IAuthorService
{
    Task<IReadOnlyList<AuthorDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Автор із книгами — Explicit Loading (два SQL-запити при loadBooks=true).</summary>
    Task<AuthorWithBooksDto> GetByIdAsync(int id, bool includeBooks, CancellationToken ct = default);

    Task<AuthorDto> CreateAsync(CreateAuthorDto input, CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>Бізнес-операції над жанрами та зв'язками M:N.</summary>
public interface IGenreService
{
    Task<IReadOnlyList<GenreDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Скільки книг у кожному жанрі — агрегат рахує БД.</summary>
    Task<IReadOnlyList<GenreStatsDto>> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// «Нові книги жанру за період» — LINQ-запит через проміжну сутність M:N
    /// з використанням її власного поля <c>AddedAt</c>.
    /// </summary>
    Task<IReadOnlyList<BookListItemDto>> GetRecentlyAddedAsync(
        string genreName, DateTime since, CancellationToken ct = default);
}
