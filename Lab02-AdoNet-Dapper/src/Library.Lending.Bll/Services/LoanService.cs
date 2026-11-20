using System.Data;
using AutoMapper;
using Library.Lending.Bll.Abstractions;
using Library.Lending.Bll.Dtos;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Domain.Constants;
using Library.Lending.Domain.Entities;
using Library.Lending.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Library.Lending.Bll.Services;

/// <summary>
/// Сервіс видач — ЄДИНИЙ власник бізнес-логіки домену.
/// Контролери API лише адаптують HTTP до цих методів і не ухвалюють рішень.
/// </summary>
public sealed class LoanService : ILoanService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;
    private readonly ILogger<LoanService> _logger;

    public LoanService(IUnitOfWork uow, IMapper mapper, ILogger<LoanService> logger)
    {
        _uow = uow;
        _mapper = mapper;
        _logger = logger;
    }

    // ------------------------------------------------------------- Читання ---

    public async Task<LoanDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var loan = await _uow.Loans.GetWithItemsAsync(id, ct)
                   ?? throw NotFoundException.For("Loan", id);
        return _mapper.Map<LoanDto>(loan);
    }

    public async Task<IReadOnlyList<LoanDto>> GetByReaderAsync(
        long readerId, string? status, CancellationToken ct = default)
    {
        if (status is not null && !LoanStatus.IsKnown(status))
            throw new ValidationException(nameof(status),
                $"Unknown status '{status}'. Allowed: {string.Join(", ", LoanStatus.All)}");

        // перевіряємо існування читача, щоб повернути 404, а не порожній список
        _ = await _uow.Readers.GetByIdAsync(readerId, ct)
            ?? throw NotFoundException.For("Reader", readerId);

        var loans = await _uow.Loans.GetByReaderAsync(readerId, status, ct);
        return loans.Select(_mapper.Map<LoanDto>).ToList();
    }

    public async Task<IReadOnlyList<LoanDto>> GetOverdueAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var loans = await _uow.Loans.GetOverdueAsync(today, ct);
        return loans.Select(_mapper.Map<LoanDto>).ToList();
    }

    public async Task<IReadOnlyList<LoanStatusHistoryDto>> GetStatusHistoryAsync(
        long loanId, CancellationToken ct = default)
    {
        _ = await _uow.Loans.GetByIdAsync(loanId, ct)
            ?? throw NotFoundException.For("Loan", loanId);

        var history = await _uow.Loans.GetStatusHistoryAsync(loanId, ct);
        return history.Select(_mapper.Map<LoanStatusHistoryDto>).ToList();
    }

    public async Task<IReadOnlyList<LoanSummaryDto>> GetReaderSummaryAsync(
        long readerId, string? status, CancellationToken ct = default)
    {
        // процедура сама кидає 50001 -> NotFoundException, якщо читача немає
        var rows = await _uow.Loans.GetReaderSummaryAsync(readerId, status, ct);
        return rows.Select(_mapper.Map<LoanSummaryDto>).ToList();
    }

    // --------------------------------------------------------- Створення ---

    /// <summary>
    /// Створення видачі. Бізнес-валідація, для якої потрібен стан із БД:
    /// читач існує й не заблокований, книги існують, примірників достатньо.
    /// Назва, автор і тариф книги беруться з <c>dbo.Books</c> і записуються в
    /// позицію як ЗНІМОК — цю частину виконує процедура <c>usp_CreateLoan</c>.
    /// </summary>
    public async Task<LoanDto> CreateAsync(CreateLoanDto input, CancellationToken ct = default)
    {
        var reader = await _uow.Readers.GetByIdAsync(input.ReaderId, ct)
                     ?? throw NotFoundException.For("Reader", input.ReaderId);

        if (reader.IsBlocked)
            throw new BusinessConflictException(
                $"Reader {reader.Id} is blocked and cannot borrow books");

        if (input.DueDate < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ValidationException(nameof(input.DueDate),
                "DueDate cannot be in the past");

        // склеюємо дублікати книг у вхідному запиті
        var items = input.Items
            .GroupBy(i => i.BookId)
            .Select(g => (BookId: g.Key, Units: g.Sum(x => x.Units)))
            .ToList();

        foreach (var (bookId, units) in items)
        {
            var book = await _uow.Books.GetByIdAsync(bookId, ct)
                       ?? throw NotFoundException.For("Book", bookId);

            if (book.CopiesAvailable < units)
                throw new BusinessConflictException(
                    $"Book '{book.Title}' has only {book.CopiesAvailable} copies available, {units} requested");
        }

        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var loanId = await _uow.Loans.CreateViaProcedureAsync(
                input.ReaderId, input.DueDate, input.PickupBranch, input.Comment,
                items, CurrentUser, ct);

            await _uow.CommitAsync(ct);

            _logger.LogInformation(
                "Loan {LoanId} created for reader {ReaderId} with {ItemCount} item(s)",
                loanId, input.ReaderId, items.Count);

            var created = await _uow.Loans.GetWithItemsAsync(loanId, ct)
                          ?? throw NotFoundException.For("Loan", loanId);
            return _mapper.Map<LoanDto>(created);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }

    // ------------------------------------------- Транзакційні бізнес-операції ---

    /// <summary>
    /// <b>ГОЛОВНИЙ транзакційний сценарій роботи.</b>
    /// </summary>
    /// <remarks>
    /// У ОДНІЙ транзакції зустрічаються Unit of Work, ДВА репозиторії та
    /// збережувані процедури з Лаб. 01:
    /// <list type="number">
    ///   <item><c>Loans.ConfirmCopiesAsync</c> → <c>usp_ConfirmLoanCopies</c>
    ///         (допустимість переходу перевіряє процедура);</item>
    ///   <item><c>Loans.IssueAsync</c> → <c>usp_IssueLoan</c>;</item>
    ///   <item><c>Books.DecreaseCopiesAsync</c> → <c>usp_DecreaseBookCopies</c>
    ///         для кожної позиції.</item>
    /// </list>
    /// Якщо на будь-якому кроці бракує примірників, процедура кидає 50003 →
    /// <see cref="BusinessConflictException"/> → <c>RollbackAsync</c>, і статус
    /// видачі повертається назад. Або все, або нічого.
    /// </remarks>
    public async Task<LoanDto> ConfirmAndIssueAsync(long loanId, CancellationToken ct = default)
    {
        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var loan = await _uow.Loans.GetWithItemsAsync(loanId, ct)
                       ?? throw NotFoundException.For("Loan", loanId);

            if (loan.Items.Count == 0)
                throw new BusinessConflictException($"Loan {loanId} has no items");

            // крок 1: -> CopiesConfirmed (перехід перевіряє процедура з Лаб. 01)
            if (loan.Status is LoanStatus.Submitted or LoanStatus.AwaitingValidation)
                await _uow.Loans.ConfirmCopiesAsync(loanId, CurrentUser, ct);

            // крок 2: -> Issued; процедура сама зменшує залишок примірників
            await _uow.Loans.IssueAsync(loanId, CurrentUser, ct);

            await _uow.CommitAsync(ct);

            _logger.LogInformation("Loan {LoanId} confirmed and issued", loanId);
        }
        catch (Exception ex)
        {
            await _uow.RollbackAsync(ct);       // статус повертається назад
            _logger.LogWarning(ex, "Failed to confirm and issue loan {LoanId}; transaction rolled back", loanId);
            throw;
        }

        return await GetByIdAsync(loanId, ct);
    }

    public async Task<LoanDto> ConfirmCopiesAsync(long loanId, CancellationToken ct = default)
    {
        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var loan = await _uow.Loans.GetWithItemsAsync(loanId, ct)
                       ?? throw NotFoundException.For("Loan", loanId);

            if (loan.Items.Count == 0)
                throw new BusinessConflictException($"Loan {loanId} has no items");

            await _uow.Loans.ConfirmCopiesAsync(loanId, CurrentUser, ct);
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(loanId, ct);
    }

    /// <summary>
    /// Повернення книг. Процедура <c>usp_ReturnLoan</c> в одній транзакції
    /// повертає примірники в довідник і рахує пеню за прострочені дні,
    /// використовуючи ЗНІМОК тарифу з позицій видачі.
    /// </summary>
    public async Task<LoanDto> ReturnAsync(long loanId, ReturnLoanDto input, CancellationToken ct = default)
    {
        if (input.ReturnDate is { } date && date > DateTime.UtcNow.AddDays(1))
            throw new ValidationException(nameof(input.ReturnDate),
                "ReturnDate cannot be in the future");

        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        decimal fine;
        try
        {
            fine = await _uow.Loans.ReturnAsync(loanId, input.ReturnDate, CurrentUser, ct);
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        if (fine > 0)
            _logger.LogInformation("Loan {LoanId} returned with fine {Fine}", loanId, fine);

        return await GetByIdAsync(loanId, ct);
    }

    public async Task<LoanDto> CancelAsync(long loanId, CancelLoanDto input, CancellationToken ct = default)
    {
        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await _uow.Loans.CancelAsync(loanId, input.Reason, CurrentUser, ct);
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(loanId, ct);
    }

    // -------------------------------------------------------- Оплата пені ---

    /// <summary>
    /// Оплата пені. ІДЕМПОТЕНТНА: якщо платіж із таким
    /// <c>IdempotencyKey</c> уже є, повертається він, а другий не створюється.
    /// </summary>
    public async Task<FinePaymentDto> PayFineAsync(
        long loanId, PayFineDto input, CancellationToken ct = default)
    {
        var loan = await _uow.Loans.GetByIdAsync(loanId, ct)
                   ?? throw NotFoundException.For("Loan", loanId);

        if (!string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            var existing = await _uow.FinePayments.FindByIdempotencyKeyAsync(input.IdempotencyKey, ct);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "Idempotent replay for key {Key}: returning existing payment {PaymentId}",
                    input.IdempotencyKey, existing.Id);
                return _mapper.Map<FinePaymentDto>(existing);
            }
        }

        if (loan.FineAmount <= 0)
            throw new BusinessConflictException($"Loan {loanId} has no outstanding fine");

        var alreadyPaid = await _uow.FinePayments.GetTotalPaidAsync(loanId, ct);
        var outstanding = loan.FineAmount - alreadyPaid;

        if (outstanding <= 0)
            throw new BusinessConflictException($"Fine for loan {loanId} is already fully paid");

        if (input.Amount > outstanding)
            throw new BusinessConflictException(
                $"Payment {input.Amount} exceeds the outstanding fine {outstanding}");

        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        long paymentId;
        try
        {
            paymentId = await _uow.FinePayments.AddAsync(new FinePayment
            {
                LoanId = loanId,
                Amount = input.Amount,
                Method = input.Method,
                IdempotencyKey = input.IdempotencyKey
            }, ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        var saved = await _uow.FinePayments.GetByIdAsync(paymentId, ct)
                    ?? throw NotFoundException.For("FinePayment", paymentId);
        return _mapper.Map<FinePaymentDto>(saved);
    }

    public async Task<IReadOnlyList<FinePaymentDto>> GetFinePaymentsAsync(
        long loanId, CancellationToken ct = default)
    {
        _ = await _uow.Loans.GetByIdAsync(loanId, ct)
            ?? throw NotFoundException.For("Loan", loanId);

        var payments = await _uow.FinePayments.GetByLoanAsync(loanId, ct);
        return payments.Select(_mapper.Map<FinePaymentDto>).ToList();
    }

    /// <summary>
    /// Поточний користувач для аудитних колонок. Автентифікація з'явиться
    /// в наступних роботах; доки її немає — технічний маркер сервісу.
    /// </summary>
    private const string CurrentUser = "lending-api";
}
