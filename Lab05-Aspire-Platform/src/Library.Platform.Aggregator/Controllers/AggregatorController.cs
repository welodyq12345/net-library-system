using Library.Platform.Aggregator.Dtos;
using Library.Platform.Aggregator.Services;
using Microsoft.AspNetCore.Mvc;

namespace Library.Platform.Aggregator.Controllers;

/// <summary>
/// Агрегувальні ендпоінти платформи.
/// </summary>
/// <remarks>
/// Префікс <c>/api/aggregator</c> збігається з маршрутом Gateway, тому
/// зовнішня адреса не змінюється: <c>http://localhost:5000/api/aggregator/...</c>.
/// </remarks>
[ApiController]
[Route("api/aggregator")]
[Produces("application/json")]
public sealed class AggregatorController(IAggregationService aggregation) : ControllerBase
{
    /// <summary>
    /// Повна картка видачі: сама видача, читач, книги з Каталогу,
    /// свіжі відгуки та зведені рейтинги цих книг.
    /// </summary>
    /// <param name="id">Ідентифікатор видачі.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    /// <response code="200">
    /// Агрегована відповідь. Якщо якийсь сервіс був недоступний, відповідь
    /// повертається ЧАСТКОВОЮ, а причина — у масиві <c>warnings</c>.
    /// </response>
    /// <response code="404">Видачі з таким ідентифікатором немає.</response>
    [HttpGet("loans/{id:long}/full")]
    [ProducesResponseType<FullLoanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FullLoanDto>> GetFullLoan(long id, CancellationToken ct)
    {
        var result = await aggregation.GetFullLoanAsync(id, ct);

        return result is null
            ? NotFound(new ProblemDetails
            {
                Title = "Видачу не знайдено",
                Detail = $"Видачі з ідентифікатором {id} немає в сервісі видач",
                Status = StatusCodes.Status404NotFound
            })
            : Ok(result);
    }

    /// <summary>
    /// Картка книги: дані Каталогу, локальна копія Сервісу видач,
    /// рейтинг і останні відгуки.
    /// </summary>
    /// <param name="id">Ідентифікатор книги в Каталозі.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    /// <response code="200">Агрегована картка (можливо, часткова).</response>
    /// <response code="404">Книги з таким ідентифікатором немає в Каталозі.</response>
    [HttpGet("books/{id:long}/card")]
    [ProducesResponseType<BookCardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookCardDto>> GetBookCard(long id, CancellationToken ct)
    {
        var result = await aggregation.GetBookCardAsync(id, ct);

        return result is null
            ? NotFound(new ProblemDetails
            {
                Title = "Книгу не знайдено",
                Detail = $"Книги з ідентифікатором {id} немає в каталозі",
                Status = StatusCodes.Status404NotFound
            })
            : Ok(result);
    }
}
