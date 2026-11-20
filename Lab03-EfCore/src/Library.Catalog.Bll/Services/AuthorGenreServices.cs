using AutoMapper;
using Library.Catalog.Bll.Abstractions;
using Library.Catalog.Bll.Dtos;
using Library.Catalog.Dal.Abstractions;
using Library.Catalog.Domain.Entities;
using Library.Catalog.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Library.Catalog.Bll.Services;

/// <summary>Сервіс авторів. Демонструє <b>Explicit Loading</b> у дії.</summary>
public sealed class AuthorService : IAuthorService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;
    private readonly ILogger<AuthorService> _logger;

    public AuthorService(IUnitOfWork uow, IMapper mapper, ILogger<AuthorService> logger)
    {
        _uow = uow;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AuthorDto>> GetAllAsync(CancellationToken ct = default)
    {
        var authors = await _uow.Authors.GetAllAsync(ct);
        return authors.Select(_mapper.Map<AuthorDto>).ToList();
    }

    /// <summary>
    /// Автор за ідентифікатором. Коли <paramref name="includeBooks"/> = true,
    /// книги довантажуються ОКРЕМИМ (другим) запитом — Explicit Loading.
    /// У лозі SQL-команд EF Core це видно як два запити замість одного.
    /// </summary>
    public async Task<AuthorWithBooksDto> GetByIdAsync(
        int id, bool includeBooks, CancellationToken ct = default)
    {
        var author = await _uow.Authors.GetWithBooksAsync(id, includeBooks, ct)
                     ?? throw EntityNotFoundException.For("Author", id);

        return _mapper.Map<AuthorWithBooksDto>(author);
    }

    public async Task<AuthorDto> CreateAsync(CreateAuthorDto input, CancellationToken ct = default)
    {
        var existing = await _uow.Authors.GetAllAsync(ct);
        if (existing.Any(a => string.Equals(a.FullName, input.FullName.Trim(),
                                            StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"Author '{input.FullName}' already exists");

        var author = new Author(input.FullName, input.Country, input.BirthYear);

        await _uow.Authors.AddAsync(author, ct);
        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Author {AuthorId} '{Name}' created", author.Id, author.FullName);
        return _mapper.Map<AuthorDto>(author);
    }

    /// <summary>
    /// М'яке видалення автора. Інваріант «автора з книгами видаляти не можна»
    /// перевіряє САМА сутність (<see cref="Author.MarkDeleted"/>), тому книги
    /// треба спершу завантажити — Explicit Loading з <c>loadBooks: true</c>.
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var author = await _uow.Authors.GetWithBooksAsync(id, loadBooks: true, ct)
                     ?? throw EntityNotFoundException.For("Author", id);

        author.MarkDeleted();                      // кидає DomainException -> 409, якщо є книги
        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Author {AuthorId} soft-deleted", id);
    }
}

/// <summary>
/// Сервіс жанрів. Демонструє <b>LINQ to Entities</b> через проміжну
/// сутність зв'язку M:N та її власне поле <c>AddedAt</c>.
/// </summary>
public sealed class GenreService : IGenreService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public GenreService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public async Task<IReadOnlyList<GenreDto>> GetAllAsync(CancellationToken ct = default)
    {
        var genres = await _uow.Genres.GetAllAsync(ct);
        return genres.Where(g => !g.IsDeleted)
                     .OrderBy(g => g.Name)
                     .Select(_mapper.Map<GenreDto>)
                     .ToList();
    }

    public async Task<IReadOnlyList<GenreStatsDto>> GetStatsAsync(CancellationToken ct = default)
    {
        var rows = await _uow.BookGenres.GetGenreStatsAsync(ct);
        return rows.Select(r => new GenreStatsDto(r.GenreId, r.GenreName, r.BookCount)).ToList();
    }

    /// <summary>
    /// «Нові книги жанру за період». Запит іде через проміжну сутність
    /// <c>BookGenre</c> і фільтрує за її ВЛАСНИМ полем <c>AddedAt</c> —
    /// автоматичний M:N в EF Core такого поля не мав би взагалі.
    /// </summary>
    public async Task<IReadOnlyList<BookListItemDto>> GetRecentlyAddedAsync(
        string genreName, DateTime since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genreName))
            throw new ValidationException(nameof(genreName), "Назва жанру обовʼязкова");

        var books = await _uow.BookGenres.GetRecentlyAddedAsync(genreName.Trim(), since, ct);
        return books.Select(_mapper.Map<BookListItemDto>).ToList();
    }
}
