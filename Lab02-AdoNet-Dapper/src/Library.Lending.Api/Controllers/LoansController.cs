using Library.Lending.Bll.Abstractions;
using Library.Lending.Bll.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Library.Lending.Api.Controllers;

/// <summary>
/// Видачі книг. <b>Тонкий контролер</b>: жодного SQL і жодного бізнес-правила —
/// лише адаптація HTTP до методів <see cref="ILoanService"/>.
/// </summary>
/// <remarks>
/// <see cref="CancellationToken"/> передається з контролера в BLL і далі в DAL;
/// у ланцюжку немає жодного <c>.Result</c> чи <c>.Wait()</c>.
/// Винятки НЕ перехоплюються тут — їх централізовано перетворює
/// <c>DomainExceptionHandler</c> на ProblemDetails із потрібним статусом.
/// </remarks>
[ApiController]
[Route("api/loans")]
[Produces("application/json")]
public sealed class LoansController : ControllerBase
{
    private readonly ILoanService _loans;

    public LoansController(ILoanService loans) => _loans = loans;

    /// <summary>Повертає видачу разом із позиціями та деталями.</summary>
    /// <response code="200">Видачу знайдено.</response>
    /// <response code="404">Видачі з таким Id не існує.</response>
    [HttpGet("{id:long}")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanDto>> GetById(long id, CancellationToken ct)
        => Ok(await _loans.GetByIdAsync(id, ct));

    /// <summary>Повертає прострочені видачі (термін вийшов, книги на руках).</summary>
    [HttpGet("overdue")]
    [ProducesResponseType<IReadOnlyList<LoanDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LoanDto>>> GetOverdue(CancellationToken ct)
        => Ok(await _loans.GetOverdueAsync(ct));

    /// <summary>Журнал переходів статусів видачі.</summary>
    [HttpGet("{id:long}/history")]
    [ProducesResponseType<IReadOnlyList<LoanStatusHistoryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LoanStatusHistoryDto>>> GetHistory(
        long id, CancellationToken ct)
        => Ok(await _loans.GetStatusHistoryAsync(id, ct));

    /// <summary>
    /// Оформлює нову видачу. Назва, автор і тариф книги фіксуються в позиціях
    /// як ЗНІМОК на момент оформлення.
    /// </summary>
    /// <response code="201">Видачу створено; посилання на неї — у заголовку Location.</response>
    /// <response code="400">Некоректні вхідні дані.</response>
    /// <response code="404">Читача або книги не існує.</response>
    /// <response code="409">Читач заблокований або бракує примірників.</response>
    [HttpPost]
    [ProducesResponseType<LoanDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LoanDto>> Create(CreateLoanDto input, CancellationToken ct)
    {
        var created = await _loans.CreateAsync(input, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Підтверджує наявність примірників і видає книги на руки
    /// в ОДНІЙ транзакції (головний транзакційний сценарій роботи).
    /// </summary>
    /// <response code="200">Видачу підтверджено й видано.</response>
    /// <response code="404">Видачі не існує.</response>
    /// <response code="409">Недопустимий перехід статусу або бракує примірників — зміни відкочено.</response>
    [HttpPost("{id:long}/issue")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LoanDto>> Issue(long id, CancellationToken ct)
        => Ok(await _loans.ConfirmAndIssueAsync(id, ct));

    /// <summary>Підтверджує наявність примірників без видачі на руки.</summary>
    /// <response code="409">Недопустимий перехід статусу або бракує примірників.</response>
    [HttpPost("{id:long}/confirm")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LoanDto>> Confirm(long id, CancellationToken ct)
        => Ok(await _loans.ConfirmCopiesAsync(id, ct));

    /// <summary>Приймає книги назад; процедура нараховує пеню за прострочені дні.</summary>
    /// <response code="409">Повернути можна лише видану на руки видачу.</response>
    [HttpPost("{id:long}/return")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LoanDto>> Return(long id, ReturnLoanDto input, CancellationToken ct)
        => Ok(await _loans.ReturnAsync(id, input, ct));

    /// <summary>Скасовує видачу. Допустимо ЛИШЕ до видачі книг на руки.</summary>
    /// <response code="409">Книги вже видані на руки — скасувати не можна.</response>
    [HttpPost("{id:long}/cancel")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LoanDto>> Cancel(long id, CancelLoanDto input, CancellationToken ct)
        => Ok(await _loans.CancelAsync(id, input, ct));

    /// <summary>Оплати пені по видачі.</summary>
    [HttpGet("{id:long}/fine-payments")]
    [ProducesResponseType<IReadOnlyList<FinePaymentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<FinePaymentDto>>> GetFinePayments(
        long id, CancellationToken ct)
        => Ok(await _loans.GetFinePaymentsAsync(id, ct));

    /// <summary>
    /// Оплачує пеню. ІДЕМПОТЕНТНА операція: повторний запит із тим самим
    /// <c>idempotencyKey</c> не створює другий платіж, а повертає наявний.
    /// </summary>
    /// <response code="201">Платіж створено.</response>
    /// <response code="409">Пені немає або сума перевищує залишок до сплати.</response>
    [HttpPost("{id:long}/fine-payments")]
    [ProducesResponseType<FinePaymentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FinePaymentDto>> PayFine(
        long id, PayFineDto input, CancellationToken ct)
    {
        var payment = await _loans.PayFineAsync(id, input, ct);
        return CreatedAtAction(nameof(GetFinePayments), new { id }, payment);
    }
}
