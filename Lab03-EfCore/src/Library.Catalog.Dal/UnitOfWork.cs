using Library.Catalog.Dal.Abstractions;
using Library.Catalog.Dal.Repositories;
using Library.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore.Storage;

namespace Library.Catalog.Dal;

/// <summary>
/// Unit of Work над EF Core.
/// </summary>
/// <remarks>
/// <para>
/// Обʼєднує всі репозиторії й надає ЄДИНИЙ <see cref="SaveChangesAsync"/>.
/// Репозиторії лише додають, змінюють або видаляють сутності в контексті;
/// у базу все потрапляє одним викликом наприкінці бізнес-операції — і EF Core
/// обгортає це в одну транзакцію автоматично.
/// </para>
/// <para>
/// Порівняння з Лаб. 02: там транзакцію доводилося відкривати й комітити
/// вручну (<c>SqlTransaction</c>), бо ADO.NET нічого про одиницю роботи не знає.
/// Тут цю роль виконує сам <c>DbContext</c> — як <c>OrderingContext</c> в eShop.
/// </para>
/// </remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly CatalogDbContext _context;

    public UnitOfWork(CatalogDbContext context)
    {
        _context = context;

        Books       = new BookRepository(context);
        Authors     = new AuthorRepository(context);
        BookGenres  = new BookGenreRepository(context);
        Genres      = new GenericRepository<Genre>(context);
        BookCopies  = new GenericRepository<BookCopy>(context);
    }

    public IBookRepository Books { get; }
    public IAuthorRepository Authors { get; }
    public IBookGenreRepository BookGenres { get; }
    public IGenericRepository<Genre> Genres { get; }
    public IGenericRepository<BookCopy> BookCopies { get; }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
        => _context.SaveChangesAsync(ct);

    /// <summary>
    /// Явна транзакція. Потрібна лише тоді, коли одна бізнес-операція робить
    /// КІЛЬКА послідовних <see cref="SaveChangesAsync"/> і їх треба звʼязати.
    /// У звичайному сценарії (один SaveChanges) викликати її не потрібно.
    /// </summary>
    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default)
    {
        var transaction = await _context.Database.BeginTransactionAsync(ct);
        return new TransactionScope(transaction);
    }

    /// <summary>
    /// Обгортка, що комітить транзакцію при штатному завершенні й відкочує її,
    /// якщо область лишили через виняток.
    /// </summary>
    private sealed class TransactionScope : IAsyncDisposable
    {
        private readonly IDbContextTransaction _transaction;
        private bool _committed;

        public TransactionScope(IDbContextTransaction transaction) => _transaction = transaction;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_committed)
                {
                    await _transaction.CommitAsync();
                    _committed = true;
                }
            }
            catch
            {
                await _transaction.RollbackAsync();
                throw;
            }
            finally
            {
                await _transaction.DisposeAsync();
            }
        }
    }
}
