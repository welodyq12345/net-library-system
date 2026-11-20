using Library.Lending.Bll.Abstractions;
using Library.Lending.Bll.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Library.Lending.Api.Controllers;

/// <summary>
/// Читачі бібліотеки. Обслуговується репозиторієм на ЧИСТОМУ ADO.NET.
/// </summary>
[ApiController]
[Route("api/readers")]
[Produces("application/json")]
public sealed class ReadersController : ControllerBase
{
    private readonly IReaderService _readers;
    private readonly ILoanService _loans;

    public ReadersController(IReaderService readers, ILoanService loans)
    {
        _readers = readers;
        _loans = loans;
    }

    /// <summary>Перелік активних читачів (без м'яко видалених).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ReaderDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReaderDto>>> GetAll(CancellationToken ct)
        => Ok(await _readers.GetAllAsync(ct));

    /// <summary>Читач за ідентифікатором.</summary>
    /// <response code="404">Читача з таким Id не існує.</response>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ReaderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReaderDto>> GetById(long id, CancellationToken ct)
        => Ok(await _readers.GetByIdAsync(id, ct));

    /// <summary>Видачі читача, за потреби відфільтровані за статусом.</summary>
    /// <param name="id">Ідентифікатор читача.</param>
    /// <param name="status">Статус видачі: Submitted, AwaitingValidation, CopiesConfirmed, Issued, Returned, Cancelled.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    /// <response code="400">Невідомий статус.</response>
    /// <response code="404">Читача не існує.</response>
    [HttpGet("{id:long}/loans")]
    [ProducesResponseType<IReadOnlyList<LoanDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LoanDto>>> GetLoans(
        long id, [FromQuery] string? status, CancellationToken ct)
        => Ok(await _loans.GetByReaderAsync(id, status, ct));

    /// <summary>Звіт по видачах читача (денормалізоване представлення з БД).</summary>
    /// <response code="404">Читача не існує (помилка 50001 з процедури).</response>
    [HttpGet("{id:long}/loan-summary")]
    [ProducesResponseType<IReadOnlyList<LoanSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LoanSummaryDto>>> GetLoanSummary(
        long id, [FromQuery] string? status, CancellationToken ct)
        => Ok(await _loans.GetReaderSummaryAsync(id, status, ct));

    /// <summary>Реєструє нового читача.</summary>
    /// <response code="201">Читача створено.</response>
    /// <response code="400">Некоректні вхідні дані.</response>
    /// <response code="409">Email або номер картки вже використовується.</response>
    [HttpPost]
    [ProducesResponseType<ReaderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReaderDto>> Create(CreateReaderDto input, CancellationToken ct)
    {
        var created = await _readers.CreateAsync(input, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>Блокує читача: заблокований не може оформлювати нові видачі.</summary>
    /// <response code="204">Прапорець установлено.</response>
    [HttpPut("{id:long}/blocked")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetBlocked(
        long id, [FromQuery] bool value, CancellationToken ct)
    {
        await _readers.SetBlockedAsync(id, value, ct);
        return NoContent();
    }

    /// <summary>
    /// М'яке видалення читача (<c>IsDeleted = 1</c>): історія видач зберігається.
    /// </summary>
    /// <response code="204">Читача позначено видаленим.</response>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await _readers.DeleteAsync(id, ct);
        return NoContent();
    }
}
