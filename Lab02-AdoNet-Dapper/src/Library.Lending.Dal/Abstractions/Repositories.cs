using Library.Lending.Domain.Entities;

namespace Library.Lending.Dal.Abstractions;

/// <summary>
/// Репозиторій читачів.
/// <b>Свідомо НЕ успадковує <see cref="IGenericRepository{T}"/></b>: його
/// реалізація написана на чистому ADO.NET і демонструє ручну роботу з
/// підключенням, параметрами та читачем даних без сторонніх бібліотек.
/// </summary>
public interface IReaderRepository
{
    Task<Reader?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<Reader?> GetByCardNumberAsync(string cardNumber, CancellationToken ct = default);

    Task<IReadOnlyList<Reader>> GetAllAsync(CancellationToken ct = default);

    Task<long> AddAsync(Reader reader, CancellationToken ct = default);

    Task<bool> SetBlockedAsync(long id, bool isBlocked, string? changedBy, CancellationToken ct = default);

    /// <summary>М'яке видалення: <c>IsDeleted = 1</c>, запис лишається в історії.</summary>
    Task<bool> SoftDeleteAsync(long id, CancellationToken ct = default);

    Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default);
}

/// <summary>
/// Репозиторій видач. Успадковує узагальнений CRUD і додає лише
/// вузькоспеціалізовані запити: JOIN з позиціями та виклики збережуваних
/// процедур із Лаб. 01.
/// </summary>
public interface ILoanRepository : IGenericRepository<Loan>
{
    /// <summary>Видача разом із позиціями — ОДНИМ запитом (Dapper multi-mapping).</summary>
    Task<Loan?> GetWithItemsAsync(long loanId, CancellationToken ct = default);

    Task<IReadOnlyList<Loan>> GetByReaderAsync(long readerId, string? status, CancellationToken ct = default);

    Task<IReadOnlyList<Loan>> GetOverdueAsync(DateOnly today, CancellationToken ct = default);

    /// <summary>Створення видачі з позиціями: <c>usp_CreateLoan</c>.</summary>
    Task<long> CreateViaProcedureAsync(
        long readerId, DateOnly dueDate, string pickupBranch, string? comment,
        IEnumerable<(long BookId, int Units)> items, string? createdBy,
        CancellationToken ct = default);

    /// <summary><c>usp_ConfirmLoanCopies</c>: → <c>CopiesConfirmed</c>.</summary>
    Task ConfirmCopiesAsync(long loanId, string? changedBy, CancellationToken ct = default);

    /// <summary><c>usp_IssueLoan</c>: → <c>Issued</c>.</summary>
    Task IssueAsync(long loanId, string? changedBy, CancellationToken ct = default);

    /// <summary><c>usp_ReturnLoan</c>: → <c>Returned</c>, повертає нараховану пеню.</summary>
    Task<decimal> ReturnAsync(long loanId, DateTime? returnDate, string? changedBy, CancellationToken ct = default);

    /// <summary><c>usp_CancelLoan</c>: → <c>Cancelled</c> (лише до видачі на руки).</summary>
    Task CancelAsync(long loanId, string? reason, string? changedBy, CancellationToken ct = default);

    /// <summary><c>usp_GetReaderLoanSummary</c>: звіт по видачах читача.</summary>
    Task<IReadOnlyList<LoanSummary>> GetReaderSummaryAsync(long readerId, string? status, CancellationToken ct = default);

    Task<IReadOnlyList<LoanStatusHistoryEntry>> GetStatusHistoryAsync(long loanId, CancellationToken ct = default);
}

/// <summary>Репозиторій локальної копії довідника книг.</summary>
public interface IBookRepository : IGenericRepository<Book>
{
    Task<IReadOnlyList<Book>> SearchAsync(string? title, CancellationToken ct = default);

    /// <summary>
    /// Зменшення залишку примірників через <c>usp_DecreaseBookCopies</c>.
    /// Це ДРУГИЙ репозиторій транзакційного сценарію: помилка тут
    /// відкочує зміну статусу видачі, зроблену першим репозиторієм.
    /// </summary>
    Task DecreaseCopiesAsync(long bookId, int quantity, CancellationToken ct = default);

    Task IncreaseCopiesAsync(long bookId, int quantity, CancellationToken ct = default);

    /// <summary>Синхронізація копії довідника з Каталогу (у Лаб. 08 — подією).</summary>
    Task UpsertFromCatalogAsync(Book book, CancellationToken ct = default);
}

/// <summary>Репозиторій оплат пені.</summary>
public interface IFinePaymentRepository : IGenericRepository<FinePayment>
{
    Task<IReadOnlyList<FinePayment>> GetByLoanAsync(long loanId, CancellationToken ct = default);

    Task<decimal> GetTotalPaidAsync(long loanId, CancellationToken ct = default);

    /// <summary>Повертає вже існуючий платіж за токеном ідемпотентності, якщо він є.</summary>
    Task<FinePayment?> FindByIdempotencyKeyAsync(string key, CancellationToken ct = default);
}
