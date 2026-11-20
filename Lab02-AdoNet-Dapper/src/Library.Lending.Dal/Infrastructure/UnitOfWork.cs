using System.Data;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Dal.Repositories;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Infrastructure;

/// <summary>
/// Реалізація Unit of Work на ADO.NET.
/// </summary>
/// <remarks>
/// <para>
/// Відкриває ОДНЕ з'єднання на всю бізнес-операцію, починає транзакцію з
/// потрібним рівнем ізоляції, координує кілька репозиторіїв у межах однієї
/// транзакції й гарантує <c>Commit</c> або <c>Rollback</c>.
/// </para>
/// <para>
/// <b>Ключова деталь:</b> усі репозиторії створюються в конструкторі й
/// отримують те саме з'єднання та <b>делегат</b> <c>() =&gt; _transaction</c>.
/// Якби ми передавали значення транзакції, репозиторії назавжди отримали б
/// <c>null</c> (на момент створення транзакції ще немає) і кожен працював би
/// поза транзакцією — відкат не охопив би їхні зміни.
/// </para>
/// <para>
/// Реєструється як <b>Scoped</b>: один екземпляр і одне з'єднання на HTTP-запит.
/// </para>
/// </remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly SqlConnection _connection;
    private SqlTransaction? _transaction;
    private bool _disposed;

    public UnitOfWork(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connection = new SqlConnection(connectionString);

        // усі репозиторії отримують ОДНЕ з'єднання і доступ до ПОТОЧНОЇ транзакції
        Readers      = new ReaderRepository(_connection, () => _transaction);
        Loans        = new LoanRepository(_connection, () => _transaction);
        Books        = new BookRepository(_connection, () => _transaction);
        FinePayments = new FinePaymentRepository(_connection, () => _transaction);
    }

    public IReaderRepository Readers { get; }
    public ILoanRepository Loans { get; }
    public IBookRepository Books { get; }
    public IFinePaymentRepository FinePayments { get; }

    public bool HasActiveTransaction => _transaction is not null;

    public async Task BeginTransactionAsync(
        IsolationLevel level = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A transaction is already in progress");

        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct);

        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(level, ct);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is null)
            throw new InvalidOperationException("There is no transaction to commit");

        try
        {
            await _transaction.CommitAsync(ct);
        }
        finally
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction is null) return;        // відкочувати нічого — не помилка

        try
        {
            await _transaction.RollbackAsync(ct);
        }
        catch (SqlException)
        {
            // з'єднання вже могло обірватися — транзакцію все одно
            // відкотить сервер; глушимо, щоб не маскувати первинну помилку
        }
        finally
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // незакомічена транзакція на цьому етапі = помилка в коді вище;
        // Dispose транзакції відкочує її автоматично
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        await _connection.DisposeAsync();        // повертає з'єднання в пул SqlClient
    }
}
