using FluentValidation;
using Library.Catalog.Bll.Abstractions;
using Library.Catalog.Bll.Dtos;
using Library.Catalog.Dal.Specifications;
using Microsoft.AspNetCore.Mvc;

namespace Library.Catalog.Api.Controllers;

/// <summary>
/// Книги каталогу. Тонкий контролер: не звертається до <c>DbContext</c>
/// і не містить бізнес-правил — лише адаптує HTTP до <see cref="IBookService"/>.
/// </summary>
[ApiController]
[Route("api/books")]
[Produces("application/json")]
public sealed class BooksController : ControllerBase
{
    private readonly IBookService _books;

    public BooksController(IBookService books) => _books = books;

    /// <summary>
    /// Список книг із пагінацією, фільтрацією та сортуванням.
    /// Усе виконується на рівні БД (<c>OFFSET … FETCH</c>), повні таблиці
    /// в памʼять не завантажуються.
    /// </summary>
    /// <param name="query">
    /// page (з 1), pageSize (1–100), search, minFee, maxFee, authorId, genre,
    /// onlyAvailable, sortBy (title|fee|year|copies), sortDir (asc|desc).
    /// </param>
    /// <param name="validator">Валідатор параметрів запиту (FluentValidation).</param>
    /// <param name="ct">Токен скасування запиту.</param>
    /// <response code="200">Сторінка результатів; метадані — у тілі та в заголовках X-Pagination-*.</response>
    /// <response code="400">Некоректні параметри (напр. недозволене поле сортування).</response>
    [HttpGet]
    [ProducesResponseType<PagedResult<BookListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<BookListItemDto>>> GetList(
        [FromQuery] BookQuery query,
        [FromServices] IValidator<BookQuery> validator,
        CancellationToken ct)
    {
        // FluentValidation викликається ЯВНО: автоматична валідація через
        // FluentValidation.AspNetCore застаріла й більше не підтримується
        var validation = await validator.ValidateAsync(query, ct);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        var result = await _books.GetListAsync(query, ct);

        // метадані також у заголовках — зручно для кешування й для клієнтів,
        // які не парсять тіло
        Response.Headers["X-Pagination-TotalCount"] = result.TotalCount.ToString();
        Response.Headers["X-Pagination-Page"] = result.Page.ToString();
        Response.Headers["X-Pagination-PageSize"] = result.PageSize.ToString();
        Response.Headers["X-Pagination-TotalPages"] = result.TotalPages.ToString();

        return Ok(result);
    }

    /// <summary>Картка книги з описом, примірниками та жанрами (Eager Loading, один SQL-запит).</summary>
    /// <response code="404">Книги з таким Id не існує.</response>
    [HttpGet("{id:long}")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookDto>> GetById(long id, CancellationToken ct)
        => Ok(await _books.GetByIdAsync(id, ct));

    /// <summary>Книги, яких немає в наявності (окрема специфікація).</summary>
    [HttpGet("out-of-stock")]
    [ProducesResponseType<IReadOnlyList<BookListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BookListItemDto>>> GetOutOfStock(CancellationToken ct)
        => Ok(await _books.GetOutOfStockAsync(ct));

    /// <summary>Топ книг жанру за slug — третя специфікація через той самий репозиторій.</summary>
    /// <param name="slug">Slug жанру, напр. <c>poetry</c>.</param>
    /// <param name="take">Скільки повернути (1–50).</param>
    /// <param name="ct">Токен скасування запиту.</param>
    [HttpGet("by-genre/{slug}")]
    [ProducesResponseType<IReadOnlyList<BookListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<BookListItemDto>>> GetByGenreSlug(
        string slug, [FromQuery] int take, CancellationToken ct)
        => Ok(await _books.GetTopByGenreSlugAsync(slug, take <= 0 ? 10 : take, ct));

    /// <summary>Додає книгу до каталогу.</summary>
    /// <response code="201">Книгу створено; посилання — у заголовку Location.</response>
    /// <response code="400">Помилки валідації (DataAnnotations або FluentValidation).</response>
    /// <response code="404">Автора або жанру не існує.</response>
    /// <response code="409">Книга з таким ISBN уже є.</response>
    [HttpPost]
    [ProducesResponseType<BookDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookDto>> Create(
        CreateBookDto input,
        [FromServices] IValidator<CreateBookDto> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        var created = await _books.CreateAsync(input, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>Змінює назву й тариф книги.</summary>
    /// <response code="409">Порушено інваріант сутності (напр. тариф ≤ 0).</response>
    [HttpPut("{id:long}")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookDto>> Update(
        long id, UpdateBookDto input,
        [FromServices] IValidator<UpdateBookDto> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        return Ok(await _books.UpdateAsync(id, input, ct));
    }

    /// <summary>Списує примірники. Інваріант «достатній залишок» перевіряє сутність.</summary>
    /// <response code="409">Примірників бракує.</response>
    [HttpPost("{id:long}/remove-stock")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookDto>> RemoveStock(
        long id, ChangeStockDto input, CancellationToken ct)
        => Ok(await _books.RemoveStockAsync(id, input, ct));

    /// <summary>Оприбутковує примірники.</summary>
    [HttpPost("{id:long}/add-stock")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookDto>> AddStock(
        long id, ChangeStockDto input, CancellationToken ct)
        => Ok(await _books.AddStockAsync(id, input, ct));

    /// <summary>Додає фізичний примірник (зв'язок 1:N) і збільшує залишок.</summary>
    /// <response code="409">Інвентарний номер уже використовується.</response>
    [HttpPost("{id:long}/copies")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookDto>> AddCopy(
        long id, AddCopyDto input,
        [FromServices] IValidator<AddCopyDto> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        return Ok(await _books.AddCopyAsync(id, input, ct));
    }

    /// <summary>Призначає книзі жанр (зв'язок M:N із власним полем AddedAt).</summary>
    /// <response code="409">Жанр уже призначено цій книзі.</response>
    [HttpPost("{id:long}/genres")]
    [ProducesResponseType<BookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookDto>> AssignGenre(
        long id, AssignGenreDto input, CancellationToken ct)
        => Ok(await _books.AssignGenreAsync(id, input, ct));

    /// <summary>М'яке видалення книги (<c>IsDeleted = 1</c>).</summary>
    /// <response code="204">Книгу позначено видаленою.</response>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await _books.DeleteAsync(id, ct);
        return NoContent();
    }
}
