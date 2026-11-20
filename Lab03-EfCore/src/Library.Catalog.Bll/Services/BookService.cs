using AutoMapper;
using Library.Catalog.Bll.Abstractions;
using Library.Catalog.Bll.Dtos;
using Library.Catalog.Dal.Abstractions;
using Library.Catalog.Dal.Specifications;
using Library.Catalog.Domain.Entities;
using Library.Catalog.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Library.Catalog.Bll.Services;

/// <summary>
/// Сервіс книг. Працює з даними ЛИШЕ через репозиторії та Unit of Work —
/// з <c>DbContext</c> напряму не спілкується.
/// </summary>
/// <remarks>
/// <b>Розподіл відповідальності за правила:</b>
/// <list type="bullet">
///   <item>правила САМОЇ сутності (ціна &gt; 0, достатній залишок) перевіряє
///         сутність своїми методами — <c>Book.ChangeDailyFee</c>,
///         <c>Book.RemoveStock</c>;</item>
///   <item>правила, для яких потрібні ІНШІ дані з БД (унікальність ISBN,
///         існування автора й жанрів), перевіряє цей сервіс.</item>
/// </list>
/// <c>SaveChangesAsync</c> викликається ОДИН раз наприкінці операції.
/// </remarks>
public sealed class BookService : IBookService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;
    private readonly ILogger<BookService> _logger;

    public BookService(IUnitOfWork uow, IMapper mapper, ILogger<BookService> logger)
    {
        _uow = uow;
        _mapper = mapper;
        _logger = logger;
    }

    /// <summary>
    /// Список книг. Фільтрація, сортування і <c>Skip/Take</c> виконуються
    /// в LINQ-виразі ДО матеріалізації, тому база повертає лише потрібну
    /// сторінку (<c>OFFSET … FETCH</c>), а <c>totalCount</c> рахує теж БД.
    /// </summary>
    public async Task<PagedResult<BookListItemDto>> GetListAsync(
        BookQuery query, CancellationToken ct = default)
    {
        var pageSize = query.SafePageSize;
        var page = query.SafePage;

        // одна специфікація — для сторінки, та сама без пагінації — для лічильника
        var pagedSpec = new BooksFilterSpec(query, applyPaging: true);
        var countSpec = new BooksFilterSpec(query, applyPaging: false);

        var total = await _uow.Books.CountAsync(countSpec, ct);
        var items = await _uow.Books.ListAsync(pagedSpec, ct);

        return new PagedResult<BookListItemDto>(
            items.Select(_mapper.Map<BookListItemDto>).ToList(), total, page, pageSize);
    }

    /// <summary>Картка книги — <b>Eager Loading</b>: усе пов'язане одним запитом.</summary>
    public async Task<BookDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var book = await _uow.Books.GetWithDetailsAsync(id, ct)
                   ?? throw EntityNotFoundException.For("Book", id);

        return _mapper.Map<BookDto>(book);
    }

    public async Task<BookDto> CreateAsync(CreateBookDto input, CancellationToken ct = default)
    {
        // правило, для якого потрібні інші дані з БД: унікальність ISBN
        if (await _uow.Books.ExistsByIsbnAsync(input.Isbn, ct))
            throw new ConflictException($"Book with ISBN '{input.Isbn}' already exists");

        if (!await _uow.Authors.ExistsAsync(input.AuthorId, ct))
            throw EntityNotFoundException.For("Author", input.AuthorId);

        foreach (var genreId in input.GenreIds.Distinct())
            if (await _uow.Genres.GetByIdAsync(genreId, ct) is null)
                throw EntityNotFoundException.For("Genre", genreId);

        // конструктор сутності сам перевіряє свої інваріанти
        var book = new Book(input.Title, input.Isbn, input.DailyFee, input.AuthorId, input.PublishedYear);

        if (!string.IsNullOrWhiteSpace(input.Description))
            book.SetDetail(new BookDetail(input.Description, input.Publisher, input.PageCount));

        var first = true;
        foreach (var genreId in input.GenreIds.Distinct())
        {
            book.AssignGenre(genreId, isPrimary: first, addedBy: "catalog-api");
            first = false;
        }

        await _uow.Books.AddAsync(book, ct);
        await _uow.SaveChangesAsync(ct);           // ОДИН раз наприкінці операції

        _logger.LogInformation("Book {BookId} '{Title}' created", book.Id, book.Title);

        return await GetByIdAsync(book.Id, ct);
    }

    public async Task<BookDto> UpdateAsync(long id, UpdateBookDto input, CancellationToken ct = default)
    {
        var book = await _uow.Books.GetByIdAsync(id, ct)
                   ?? throw EntityNotFoundException.For("Book", id);

        // зміни проходять лише через методи сутності — setter-ів немає
        book.Rename(input.Title);
        book.ChangeDailyFee(input.DailyFee);

        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Book {BookId} updated", id);
        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// Списання примірників. Якщо залишку бракує, <see cref="Book.RemoveStock"/>
    /// кидає <see cref="DomainException"/> → HTTP 409, і <c>SaveChangesAsync</c>
    /// навіть не викликається: у базі нічого не змінюється.
    /// </summary>
    public async Task<BookDto> RemoveStockAsync(long id, ChangeStockDto input, CancellationToken ct = default)
    {
        var book = await _uow.Books.GetByIdAsync(id, ct)
                   ?? throw EntityNotFoundException.For("Book", id);

        book.RemoveStock(input.Quantity);
        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Book {BookId}: {Quantity} copies removed from stock", id, input.Quantity);
        return await GetByIdAsync(id, ct);
    }

    public async Task<BookDto> AddStockAsync(long id, ChangeStockDto input, CancellationToken ct = default)
    {
        var book = await _uow.Books.GetByIdAsync(id, ct)
                   ?? throw EntityNotFoundException.For("Book", id);

        book.AddStock(input.Quantity);
        await _uow.SaveChangesAsync(ct);

        return await GetByIdAsync(id, ct);
    }

    public async Task<BookDto> AddCopyAsync(long id, AddCopyDto input, CancellationToken ct = default)
    {
        // книга потрібна РАЗОМ із примірниками: інваріант «інвентарний номер
        // унікальний у межах книги» перевіряє сама сутність в AddCopy, і без
        // завантаженої колекції Copies перевірка «не побачила б» наявні рядки
        var book = await _uow.Books.GetWithCopiesAsync(id, ct)
                   ?? throw EntityNotFoundException.For("Book", id);

        var copy = new BookCopy(input.InventoryNumber, input.Shelf, input.Condition, input.AcquiredAt);
        book.AddCopy(copy);                        // + AddStock(1) всередині

        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Book {BookId}: copy {Inventory} added", id, input.InventoryNumber);
        return await GetByIdAsync(id, ct);
    }

    public async Task<BookDto> AssignGenreAsync(long id, AssignGenreDto input, CancellationToken ct = default)
    {
        // потрібні вже призначені жанри, щоб сутність могла перевірити дублікат
        var book = await _uow.Books.GetWithGenresAsync(id, ct)
                   ?? throw EntityNotFoundException.For("Book", id);

        if (await _uow.Genres.GetByIdAsync(input.GenreId, ct) is null)
            throw EntityNotFoundException.For("Genre", input.GenreId);

        book.AssignGenre(input.GenreId, input.IsPrimary, addedBy: "catalog-api");
        await _uow.SaveChangesAsync(ct);

        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var book = await _uow.Books.GetByIdAsync(id, ct)
                   ?? throw EntityNotFoundException.For("Book", id);

        book.MarkDeleted();                        // м'яке видалення
        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Book {BookId} soft-deleted", id);
    }

    /// <summary>Друга специфікація через той самий <c>ListAsync</c> — без змін у репозиторії.</summary>
    public async Task<IReadOnlyList<BookListItemDto>> GetOutOfStockAsync(CancellationToken ct = default)
    {
        var items = await _uow.Books.ListAsync(new OutOfStockBooksSpec(), ct);
        return items.Select(_mapper.Map<BookListItemDto>).ToList();
    }

    /// <summary>Третя специфікація — теж без змін у репозиторії.</summary>
    public async Task<IReadOnlyList<BookListItemDto>> GetTopByGenreSlugAsync(
        string slug, int take, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ValidationException(nameof(slug), "Slug жанру не може бути порожнім");

        var items = await _uow.Books.ListAsync(
            new BooksByGenreSlugSpec(slug.Trim().ToLowerInvariant(), Math.Clamp(take, 1, 50)), ct);

        return items.Select(_mapper.Map<BookListItemDto>).ToList();
    }
}
