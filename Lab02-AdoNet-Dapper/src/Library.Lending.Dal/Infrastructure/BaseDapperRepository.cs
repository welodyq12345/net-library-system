using Dapper;
using Library.Lending.Dal.Abstractions;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Infrastructure;

/// <summary>
/// Спільна реалізація CRUD на Dapper, написана ОДИН раз.
/// Конкретні репозиторії успадковують її й додають лише свої специфічні запити.
/// </summary>
/// <remarks>
/// <para>
/// <b>Безпека підстановки імені таблиці.</b> <paramref name="tableName"/> і
/// перелік стовпців приходять із КОДУ конкретного репозиторію (константи), а не
/// з вхідних даних користувача, тому їх підстановка в текст запиту безпечна.
/// Усі ЗНАЧЕННЯ передаються виключно як параметри — жодної конкатенації з
/// користувацьким вводом (OWASP SQL Injection Prevention).
/// </para>
/// <para>
/// Dapper — мікро-ORM, що РОЗШИРЮЄ ADO.NET, а не замінює його: з'єднання,
/// команди й транзакції лишаються тими самими об'єктами
/// <c>Microsoft.Data.SqlClient</c>.
/// </para>
/// </remarks>
public abstract class BaseDapperRepository<T> : RepositoryBase, IGenericRepository<T>
    where T : class
{
    /// <summary>Повне ім'я таблиці, напр. <c>dbo.Loans</c>. Константа з коду.</summary>
    protected readonly string TableName;

    /// <summary>Ім'я стовпця первинного ключа.</summary>
    protected readonly string KeyColumn;

    protected BaseDapperRepository(
        SqlConnection connection,
        Func<SqlTransaction?> currentTransaction,
        string tableName,
        string keyColumn = "Id")
        : base(connection, currentTransaction)
    {
        TableName = tableName;
        KeyColumn = keyColumn;
    }

    /// <summary>Стовпці, які читаються за замовчуванням (константа з коду конкретного репозиторію).</summary>
    protected abstract string SelectColumns { get; }

    /// <summary>Фільтр «живих» записів; порожній рядок, якщо soft delete не застосовується.</summary>
    protected virtual string NotDeletedFilter => string.Empty;

    private string AndNotDeleted =>
        string.IsNullOrEmpty(NotDeletedFilter) ? string.Empty : $" AND {NotDeletedFilter}";

    private string WhereNotDeleted =>
        string.IsNullOrEmpty(NotDeletedFilter) ? string.Empty : $" WHERE {NotDeletedFilter}";

    /// <summary>Створює <see cref="CommandDefinition"/> із поточною транзакцією Unit of Work.</summary>
    protected CommandDefinition Command(
        string sql, object? param, CancellationToken ct,
        System.Data.CommandType? commandType = null) =>
        new(sql, param, Transaction, commandType: commandType, cancellationToken: ct);

    public virtual async Task<T?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM {TableName} WHERE {KeyColumn} = @Id{AndNotDeleted}";

        await EnsureOpenAsync(ct);
        return await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QuerySingleOrDefaultAsync<T>(Command(sql, new { Id = id }, ct)),
            $"{TableName}.GetById");
    }

    public virtual async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM {TableName}{WhereNotDeleted} ORDER BY {KeyColumn}";

        await EnsureOpenAsync(ct);
        var rows = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QueryAsync<T>(Command(sql, null, ct)),
            $"{TableName}.GetAll");
        return rows.ToList();
    }

    public abstract Task<long> AddAsync(T entity, CancellationToken ct = default);

    public abstract Task<bool> UpdateAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// Видалення. Якщо для таблиці налаштовано soft delete
    /// (<see cref="NotDeletedFilter"/> не порожній), виконується саме воно.
    /// </summary>
    public virtual async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var sql = string.IsNullOrEmpty(NotDeletedFilter)
            ? $"DELETE FROM {TableName} WHERE {KeyColumn} = @Id"
            : $"UPDATE {TableName} SET IsDeleted = 1, UpdatedAt = SYSUTCDATETIME() WHERE {KeyColumn} = @Id AND IsDeleted = 0";

        await EnsureOpenAsync(ct);
        var affected = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(sql, new { Id = id }, ct)),
            $"{TableName}.Delete");
        return affected > 0;
    }
}
