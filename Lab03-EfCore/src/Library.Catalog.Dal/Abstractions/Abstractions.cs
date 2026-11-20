using Ardalis.Specification;
using Library.Catalog.Domain.Entities;

namespace Library.Catalog.Dal.Abstractions;

/// <summary>
/// Асинхронний узагальнений репозиторій. Той самий контракт, що в Лаб. 02,
/// але тепер його реалізує EF Core через <c>DbSet&lt;T&gt;</c>.
/// </summary>
/// <remarks>
/// <see cref="Update"/> і <see cref="Delete"/> лишаються СИНХРОННИМИ: вони
/// лише змінюють стан сутності в контексті й до бази не звертаються. У базу
/// все потрапляє одним викликом <c>SaveChangesAsync</c> наприкінці операції.
/// </remarks>
public interface IGenericRepository<T> where T : class
{
    Task<T?> GetByIdAsync(object id, CancellationToken ct = default);

    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(T entity, CancellationToken ct = default);

    void Update(T entity);

    void Delete(T entity);

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Вибірка за специфікацією (Specification pattern, Ardalis).</summary>
    Task<IReadOnlyList<T>> ListAsync(ISpecification<T> specification, CancellationToken ct = default);

    /// <summary>Кількість записів, що відповідають специфікації (рахує БД).</summary>
    Task<int> CountAsync(ISpecification<T> specification, CancellationToken ct = default);
}

/// <summary>
/// Репозиторій книг. Демонструє <b>Eager Loading</b>: пов'язані дані
/// завантажуються ОДНИМ запитом разом з основною сутністю (Include/ThenInclude).
/// </summary>
public interface IBookRepository : IGenericRepository<Book>
{
    /// <summary>Картка книги з усім пов'язаним — <b>Eager Loading</b>, один SQL-запит.</summary>
    Task<Book?> GetWithDetailsAsync(long id, CancellationToken ct = default);

    Task<bool> ExistsByIsbnAsync(string isbn, CancellationToken ct = default);

    Task<bool> ExistsByIsbnAsync(string isbn, long excludeBookId, CancellationToken ct = default);

    /// <summary>Книга з жанрами — для операцій, де примірники не потрібні.</summary>
    Task<Book?> GetWithGenresAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Книга РАЗОМ із примірниками — відстежувана.
    /// Потрібна там, де інваріант сутності перевіряє вже наявні примірники
    /// (<see cref="Book.AddCopy"/> не дозволяє повторний інвентарний номер):
    /// без завантаженої колекції перевірка «не побачила б» існуючі рядки.
    /// </summary>
    Task<Book?> GetWithCopiesAsync(long id, CancellationToken ct = default);
}

/// <summary>
/// Репозиторій авторів. Демонструє <b>Explicit Loading</b>: основна сутність
/// завантажується окремо, а пов'язані дані — окремим запитом лише тоді,
/// коли вони справді потрібні.
/// </summary>
public interface IAuthorRepository : IGenericRepository<Author>
{
    /// <summary>Автор із книгами — <b>Explicit Loading</b>, ДВА SQL-запити.</summary>
    Task<Author?> GetWithBooksAsync(int id, bool loadBooks, CancellationToken ct = default);

    Task<bool> ExistsAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Репозиторій зв'язків «книга–жанр». Демонструє <b>LINQ to Entities</b>:
/// вибірку «багато до багатьох» через проміжну сутність із використанням
/// її власного поля <c>AddedAt</c>.
/// </summary>
public interface IBookGenreRepository : IGenericRepository<BookGenre>
{
    /// <summary>
    /// «Нові книги жанру за період» — LINQ-запит через проміжну сутність.
    /// Саме цей запит обслуговує індекс <c>IX_BookGenres_GenreId_AddedAt</c>.
    /// </summary>
    Task<IReadOnlyList<Book>> GetRecentlyAddedAsync(
        string genreName, DateTime since, CancellationToken ct = default);

    /// <summary>Скільки книг у кожному жанрі (агрегат рахує БД).</summary>
    Task<IReadOnlyList<(int GenreId, string GenreName, int BookCount)>> GetGenreStatsAsync(
        CancellationToken ct = default);

    Task<BookGenre?> FindAsync(long bookId, int genreId, CancellationToken ct = default);
}

/// <summary>
/// Unit of Work над EF Core.
/// </summary>
/// <remarks>
/// На відміну від Лаб. 02, тут транзакцію вручну відкривати не потрібно:
/// <c>DbContext</c> сам є одиницею роботи. Репозиторії лише додають, змінюють
/// чи видаляють сутності в контексті, а <see cref="SaveChangesAsync"/>
/// викликається ОДИН раз наприкінці бізнес-операції — і всі зміни потрапляють
/// у базу в одній транзакції.
/// У eShop ту саму роль виконує сам <c>OrderingContext</c>, який реалізує
/// <c>IUnitOfWork</c> з <c>Ordering.Domain/SeedWork</c>.
/// </remarks>
public interface IUnitOfWork
{
    IBookRepository Books { get; }
    IAuthorRepository Authors { get; }
    IBookGenreRepository BookGenres { get; }
    IGenericRepository<Genre> Genres { get; }
    IGenericRepository<BookCopy> BookCopies { get; }

    /// <summary>Зберігає ВСІ зміни контексту однією транзакцією.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Явна транзакція — потрібна лише тоді, коли одна бізнес-операція робить
    /// кілька послідовних <c>SaveChangesAsync</c> (рідкісний випадок).
    /// </summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default);
}
