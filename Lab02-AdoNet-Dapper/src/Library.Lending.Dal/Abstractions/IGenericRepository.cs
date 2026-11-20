namespace Library.Lending.Dal.Abstractions;

/// <summary>
/// Узагальнений репозиторій: однакові CRUD-методи, винесені з конкретних
/// інтерфейсів, щоб не повторювати їх для кожної сутності.
/// </summary>
/// <remarks>
/// Усі методи, що звертаються до БД, асинхронні й приймають
/// <see cref="CancellationToken"/>. Методи повертають моделі домену —
/// ЖОДЕН не повертає «сирі» <c>SqlDataReader</c>, <c>DataTable</c>
/// чи рядки SQL: це вимога інкапсуляції DAL.
/// </remarks>
public interface IGenericRepository<T> where T : class
{
    Task<T?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);

    Task<long> AddAsync(T entity, CancellationToken ct = default);

    Task<bool> UpdateAsync(T entity, CancellationToken ct = default);

    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
}
