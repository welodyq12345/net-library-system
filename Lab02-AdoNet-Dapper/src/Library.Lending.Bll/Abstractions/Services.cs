using Library.Lending.Bll.Dtos;

namespace Library.Lending.Bll.Abstractions;

/// <summary>Бізнес-операції над читачами.</summary>
public interface IReaderService
{
    Task<ReaderDto> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<ReaderDto>> GetAllAsync(CancellationToken ct = default);
    Task<ReaderDto> CreateAsync(CreateReaderDto input, CancellationToken ct = default);
    Task SetBlockedAsync(long id, bool isBlocked, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>Бізнес-операції над локальною копією довідника книг.</summary>
public interface IBookService
{
    Task<BookDto> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<BookDto>> SearchAsync(string? title, CancellationToken ct = default);
    Task<BookDto> SyncFromCatalogAsync(SyncBookDto input, CancellationToken ct = default);
}

/// <summary>
/// Бізнес-операції над видачами — ядро домену.
/// Усі операції, що змінюють кілька таблиць, виконуються в ОДНІЙ транзакції
/// через Unit of Work.
/// </summary>
public interface ILoanService
{
    Task<LoanDto> GetByIdAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<LoanDto>> GetByReaderAsync(long readerId, string? status, CancellationToken ct = default);

    Task<IReadOnlyList<LoanDto>> GetOverdueAsync(CancellationToken ct = default);

    Task<LoanDto> CreateAsync(CreateLoanDto input, CancellationToken ct = default);

    /// <summary>
    /// ТРАНЗАКЦІЙНА бізнес-операція: підтвердження наявності примірників
    /// і видача книг на руки зі зменшенням залишку — або все, або нічого.
    /// </summary>
    Task<LoanDto> ConfirmAndIssueAsync(long loanId, CancellationToken ct = default);

    Task<LoanDto> ConfirmCopiesAsync(long loanId, CancellationToken ct = default);

    Task<LoanDto> ReturnAsync(long loanId, ReturnLoanDto input, CancellationToken ct = default);

    Task<LoanDto> CancelAsync(long loanId, CancelLoanDto input, CancellationToken ct = default);

    Task<IReadOnlyList<LoanStatusHistoryDto>> GetStatusHistoryAsync(long loanId, CancellationToken ct = default);

    Task<IReadOnlyList<LoanSummaryDto>> GetReaderSummaryAsync(long readerId, string? status, CancellationToken ct = default);

    /// <summary>Оплата пені з підтримкою ідемпотентності.</summary>
    Task<FinePaymentDto> PayFineAsync(long loanId, PayFineDto input, CancellationToken ct = default);

    Task<IReadOnlyList<FinePaymentDto>> GetFinePaymentsAsync(long loanId, CancellationToken ct = default);
}
