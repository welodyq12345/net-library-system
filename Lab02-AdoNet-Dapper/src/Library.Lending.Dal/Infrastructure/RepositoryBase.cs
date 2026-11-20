using System.Data;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Infrastructure;

/// <summary>
/// Спільний базовий клас усіх репозиторіїв.
/// </summary>
/// <remarks>
/// Репозиторій отримує в конструкторі НЕ рядок підключення, а вже створене
/// з'єднання та <b>делегат доступу до поточної транзакції</b>. Саме завдяки
/// делегату (а не значенню) усі репозиторії однієї бізнес-операції бачать ту
/// саму <see cref="SqlTransaction"/>, навіть якщо її відкрили вже після
/// створення репозиторіїв у конструкторі Unit of Work.
/// </remarks>
public abstract class RepositoryBase
{
    protected readonly SqlConnection Connection;
    private readonly Func<SqlTransaction?> _currentTransaction;

    protected RepositoryBase(SqlConnection connection, Func<SqlTransaction?> currentTransaction)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _currentTransaction = currentTransaction ?? throw new ArgumentNullException(nameof(currentTransaction));
    }

    /// <summary>Поточна транзакція Unit of Work (або <c>null</c>, якщо її не розпочато).</summary>
    protected SqlTransaction? Transaction => _currentTransaction();

    /// <summary>
    /// Відкриває з'єднання, якщо воно ще закрите.
    /// Dapper сам з'єднання не відкриває — він лише додає методи-розширення
    /// до вже створеного <c>DbConnection</c>, тому цей виклик потрібен і для нього.
    /// </summary>
    protected async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (Connection.State != ConnectionState.Open)
            await Connection.OpenAsync(ct);
    }
}
