using System.Data;
using AutoMapper;
using Library.Lending.Bll.Abstractions;
using Library.Lending.Bll.Dtos;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Domain.Entities;
using Library.Lending.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Library.Lending.Bll.Services;

/// <summary>
/// Сервіс локальної копії довідника книг.
/// </summary>
/// <remarks>
/// Цей контекст НЕ є власником даних про книги — власник БД Каталогу
/// (Проєкт №2). Тому тут немає операцій створення чи видалення книги:
/// є лише <see cref="SyncFromCatalogAsync"/>, яка оновлює локальну копію.
/// У Лаб. 08 цей метод викликатиметься обробником події з Каталогу.
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

    public async Task<BookDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var book = await _uow.Books.GetByIdAsync(id, ct)
                   ?? throw NotFoundException.For("Book", id);
        return _mapper.Map<BookDto>(book);
    }

    public async Task<IReadOnlyList<BookDto>> SearchAsync(string? title, CancellationToken ct = default)
    {
        var books = await _uow.Books.SearchAsync(title, ct);
        return books.Select(_mapper.Map<BookDto>).ToList();
    }

    /// <summary>
    /// Оновлення локальної копії довідника даними з Каталогу.
    /// <c>CopiesAvailable</c> зберігається лише при ПЕРШІЙ вставці: далі це
    /// власні дані контексту Видачі (таблиця політики дублювання, рядок 3).
    /// </summary>
    public async Task<BookDto> SyncFromCatalogAsync(SyncBookDto input, CancellationToken ct = default)
    {
        var entity = _mapper.Map<Book>(input);

        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await _uow.Books.UpsertFromCatalogAsync(entity, ct);
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        _logger.LogInformation(
            "Local catalog copy for book {BookId} synced from Catalog service", input.Id);

        var saved = await _uow.Books.GetByIdAsync(input.Id, ct)
                    ?? throw NotFoundException.For("Book", input.Id);
        return _mapper.Map<BookDto>(saved);
    }
}
