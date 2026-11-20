using Library.Lending.Bll.Abstractions;
using Library.Lending.Bll.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Library.Lending.Api.Controllers;

/// <summary>
/// Локальна копія довідника книг цього мікросервісу.
/// </summary>
/// <remarks>
/// Власник даних про книги — сервіс <b>Каталогу</b> (Проєкт №2, Лаб. 03).
/// Тому тут немає створення чи видалення книги: лише читання та
/// <c>PUT /api/books/sync</c>, яка оновлює локальну копію. У Лаб. 08 цей
/// ендпоінт замінить обробник події <c>BookUpdated</c> з Каталогу.
/// </remarks>
[ApiController]
[Route("api/books")]
[Produces("application/json")]
public sealed class BooksController : ControllerBase
{
    private readonly IBookService _books;

    public BooksController(IBookService books) => _books = books;

    /// <summary>Пошук книг у локальній копії довідника за частиною назви.</summary>
    /// <param name="title">Частина назви; якщо не вказано — повертаються всі книги.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<BookDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BookDto>>> Search(
        [FromQuery] string? title, CancellationToken ct)
        => Ok(await _books.SearchAsync(title, ct));

    /// <summary>Книга за ідентифікатором (той самий Id, що й у БД Каталогу).</summary>
    /// <response code="404">Книги з таким Id немає в локальній копії.</response>
    [HttpGet("{id:long}")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookDto>> GetById(long id, CancellationToken ct)
        => Ok(await _books.GetByIdAsync(id, ct));

    /// <summary>
    /// Синхронізує локальну копію довідника даними з сервісу Каталогу.
    /// Залишок примірників (<c>copiesAvailable</c>) НЕ перетирається для
    /// існуючих книг: це власні дані контексту Видачі.
    /// </summary>
    /// <response code="200">Копію оновлено або створено.</response>
    /// <response code="400">Некоректні вхідні дані.</response>
    [HttpPut("sync")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BookDto>> Sync(SyncBookDto input, CancellationToken ct)
        => Ok(await _books.SyncFromCatalogAsync(input, ct));
}
