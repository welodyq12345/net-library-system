using FluentValidation;
using Library.Catalog.Bll.Abstractions;
using Library.Catalog.Bll.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Library.Catalog.Api.Controllers;

/// <summary>Автори каталогу. Демонструє <b>Explicit Loading</b>.</summary>
[ApiController]
[Route("api/authors")]
[Produces("application/json")]
public sealed class AuthorsController : ControllerBase
{
    private readonly IAuthorService _authors;

    public AuthorsController(IAuthorService authors) => _authors = authors;

    /// <summary>Перелік активних авторів.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AuthorDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuthorDto>>> GetAll(CancellationToken ct)
        => Ok(await _authors.GetAllAsync(ct));

    /// <summary>
    /// Автор за ідентифікатором. Параметр <paramref name="includeBooks"/>
    /// керує <b>Explicit Loading</b>: при <c>true</c> книги довантажуються
    /// ДРУГИМ окремим запитом, при <c>false</c> виконується лише один.
    /// </summary>
    /// <param name="id">Ідентифікатор автора.</param>
    /// <param name="includeBooks">Чи довантажувати книги другим запитом.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    /// <response code="404">Автора з таким Id не існує.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType<AuthorWithBooksDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthorWithBooksDto>> GetById(
        int id, [FromQuery] bool includeBooks, CancellationToken ct)
        => Ok(await _authors.GetByIdAsync(id, includeBooks, ct));

    /// <summary>Додає автора.</summary>
    /// <response code="201">Автора створено.</response>
    /// <response code="409">Автор із таким імʼям уже є.</response>
    [HttpPost]
    [ProducesResponseType<AuthorDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthorDto>> Create(
        CreateAuthorDto input,
        [FromServices] IValidator<CreateAuthorDto> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        var created = await _authors.CreateAsync(input, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// М'яке видалення автора. Інваріант «автора з книгами видаляти не можна»
    /// перевіряє сама сутність.
    /// </summary>
    /// <response code="204">Автора позначено видаленим.</response>
    /// <response code="409">В автора ще є книги.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _authors.DeleteAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Жанри та статистика за зв'язком M:N.</summary>
[ApiController]
[Route("api/genres")]
[Produces("application/json")]
public sealed class GenresController : ControllerBase
{
    private readonly IGenreService _genres;

    public GenresController(IGenreService genres) => _genres = genres;

    /// <summary>Довідник жанрів (наповнений статичним сідінгом HasData).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<GenreDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GenreDto>>> GetAll(CancellationToken ct)
        => Ok(await _genres.GetAllAsync(ct));

    /// <summary>Скільки книг у кожному жанрі. <c>GROUP BY</c> виконує база даних.</summary>
    [HttpGet("stats")]
    [ProducesResponseType<IReadOnlyList<GenreStatsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GenreStatsDto>>> GetStats(CancellationToken ct)
        => Ok(await _genres.GetStatsAsync(ct));

    /// <summary>
    /// Нові книги жанру за період — <b>LINQ-запит через проміжну сутність M:N</b>
    /// з використанням її власного поля <c>AddedAt</c>.
    /// </summary>
    /// <param name="name">Назва жанру, напр. «Поезія».</param>
    /// <param name="since">Від якої дати (UTC). За замовчуванням — рік тому.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    /// <response code="400">Назву жанру не вказано.</response>
    [HttpGet("{name}/recent-books")]
    [ProducesResponseType<IReadOnlyList<BookListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<BookListItemDto>>> GetRecentBooks(
        string name, [FromQuery] DateTime? since, CancellationToken ct)
        => Ok(await _genres.GetRecentlyAddedAsync(
                name, since ?? DateTime.UtcNow.AddYears(-1), ct));
}
