using System.Runtime.ExceptionServices;
using Library.Lending.Domain.Constants;
using Library.Lending.Domain.Exceptions;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Infrastructure;

/// <summary>
/// Перетворює помилки БД на зрозумілі доменні винятки.
/// </summary>
/// <remarks>
/// Це перша ланка ланцюжка «збережувана процедура → DAL → BLL → HTTP-статус»:
/// <list type="bullet">
///   <item><c>THROW 50001</c> → <see cref="NotFoundException"/> → 404</item>
///   <item><c>THROW 50002/50003/50004</c> → <see cref="BusinessConflictException"/> → 409</item>
///   <item>порушення UNIQUE (2601, 2627) → <see cref="BusinessConflictException"/> → 409</item>
///   <item>порушення FK / CHECK (547) → <see cref="BusinessConflictException"/> → 409</item>
/// </list>
/// «Сирі» повідомлення SQL Server за межі DAL не передаються.
/// <para>
/// Помилки, яких немає в цьому переліку (обрив з'єднання, таймаут, deadlock),
/// <b>НЕ</b> перетворюються: вони підіймаються як є, бо це інфраструктурні збої,
/// а не порушення бізнес-правил, і мають стати <b>500</b>, а не 409.
/// </para>
/// </remarks>
public static class SqlExceptionTranslator
{
    /// <summary>Виконує операцію, перекладаючи відомі <see cref="SqlException"/> на доменні винятки.</summary>
    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string context)
    {
        try
        {
            return await operation();
        }
        catch (SqlException ex)
        {
            Rethrow(ex, context);
            throw;              // недосяжно: Rethrow завжди кидає
        }
    }

    /// <inheritdoc cref="ExecuteAsync{T}"/>
    public static async Task ExecuteAsync(Func<Task> operation, string context)
    {
        try
        {
            await operation();
        }
        catch (SqlException ex)
        {
            Rethrow(ex, context);
        }
    }

    /// <summary>
    /// Повертає доменний виняток для ВІДОМОГО номера помилки або <c>null</c>,
    /// якщо помилку слід передати далі без змін.
    /// </summary>
    public static DomainException? TryTranslate(SqlException ex, string context) => ex.Number switch
    {
        SqlErrorCodes.NotFound =>
            new NotFoundException(ex.Message, ex),

        SqlErrorCodes.InvalidTransition or SqlErrorCodes.NotEnoughCopies or SqlErrorCodes.BusinessRule =>
            new BusinessConflictException(ex.Message, ex),

        SqlErrorCodes.UniqueIndexViolation or SqlErrorCodes.UniqueConstraintViolation =>
            new BusinessConflictException("Record with the same unique key already exists", ex),

        // 547 — і FOREIGN KEY, і CHECK: розрізняємо за текстом повідомлення
        SqlErrorCodes.ForeignKeyViolation when ex.Message.Contains("CHECK", StringComparison.OrdinalIgnoreCase) =>
            new BusinessConflictException($"Operation violates a database CHECK constraint ({context})", ex),

        SqlErrorCodes.ForeignKeyViolation =>
            new BusinessConflictException($"Referenced record does not exist or is still in use ({context})", ex),

        _ => null               // інфраструктурний збій -> не перекладаємо
    };

    private static void Rethrow(SqlException ex, string context)
    {
        var translated = TryTranslate(ex, context);
        if (translated is not null)
            throw translated;

        // зберігаємо оригінальний stack trace інфраструктурної помилки
        ExceptionDispatchInfo.Capture(ex).Throw();
    }
}
