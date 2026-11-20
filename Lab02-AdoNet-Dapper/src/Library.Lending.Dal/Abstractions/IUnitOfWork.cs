using System.Data;

namespace Library.Lending.Dal.Abstractions;

/// <summary>
/// Unit of Work: одне з'єднання на всю бізнес-операцію, одна транзакція
/// для кількох репозиторіїв, гарантований <c>Commit</c> або <c>Rollback</c>.
/// </summary>
/// <remarks>
/// Реєструється як <b>Scoped</b> — один екземпляр і одне з'єднання на HTTP-запит.
/// В eShop аналогічний контракт — <c>IUnitOfWork</c> у
/// <c>Ordering.Domain/SeedWork</c>, де його реалізує <c>DbContext</c> EF Core;
/// тут він реалізований вручну на ADO.NET (а в Лаб. 03 — на EF Core).
/// </remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    IReaderRepository Readers { get; }
    ILoanRepository Loans { get; }
    IBookRepository Books { get; }
    IFinePaymentRepository FinePayments { get; }

    /// <summary>Чи відкрита транзакція зараз.</summary>
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Починає транзакцію. Рівень ізоляції за замовчуванням —
    /// <see cref="IsolationLevel.ReadCommitted"/>: компроміс між аномаліями
    /// читання та кількістю блокувань (обґрунтування — у README).
    /// </summary>
    Task BeginTransactionAsync(
        IsolationLevel level = IsolationLevel.ReadCommitted,
        CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);

    Task RollbackAsync(CancellationToken ct = default);
}
