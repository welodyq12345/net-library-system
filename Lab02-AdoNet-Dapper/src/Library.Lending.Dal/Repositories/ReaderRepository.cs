using System.Data;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Dal.Infrastructure;
using Library.Lending.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Repositories;

/// <summary>
/// Репозиторій читачів на <b>ЧИСТОМУ ADO.NET</b> — без Dapper і будь-яких
/// інших сторонніх бібліотек.
/// </summary>
/// <remarks>
/// <para>
/// Демонструє ручну роботу з <see cref="SqlConnection"/>,
/// <see cref="SqlCommand"/>, <see cref="SqlParameter"/> та
/// <see cref="SqlDataReader"/>: явна параметризація з указанням
/// <see cref="SqlDbType"/>, ручний мапінг рядків на об'єкти, звільнення
/// ресурсів через <c>await using</c>.
/// </para>
/// <para>
/// <b>Свідомо НЕ успадковує</b> <see cref="BaseDapperRepository{T}"/> та
/// <see cref="IGenericRepository{T}"/>: сенс цього класу — показати, як те саме
/// робиться «руками», щоб було з чим порівняти Dapper-репозиторії.
/// </para>
/// <para>
/// Усі значення передаються ЛИШЕ як параметри. Конкатенації рядків із вхідними
/// даними немає: саме тому ім'я <c>Liam O'Brien</c> зі seed-даних не ламає
/// синтаксис запиту.
/// </para>
/// </remarks>
public sealed class ReaderRepository : RepositoryBase, IReaderRepository
{
    private const string SelectColumns = """
        Id, CardNumber, Email, FullName, Phone, IsBlocked,
        CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted
        """;

    public ReaderRepository(SqlConnection connection, Func<SqlTransaction?> currentTransaction)
        : base(connection, currentTransaction) { }

    public async Task<Reader?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM   dbo.Readers
            WHERE  Id = @Id AND IsDeleted = 0
            """;

        await EnsureOpenAsync(ct);
        await using var command = new SqlCommand(sql, Connection, Transaction);
        command.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;   // ЛИШЕ параметр

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return Map(reader);
        }
        catch (SqlException ex)
        {
            throw SqlExceptionTranslator.TryTranslate(ex, "Readers.GetById") ?? (Exception)ex;
        }
    }

    public async Task<Reader?> GetByCardNumberAsync(string cardNumber, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM   dbo.Readers
            WHERE  CardNumber = @CardNumber AND IsDeleted = 0
            """;

        await EnsureOpenAsync(ct);
        await using var command = new SqlCommand(sql, Connection, Transaction);
        command.Parameters.Add("@CardNumber", SqlDbType.NVarChar, 20).Value = cardNumber;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return Map(reader);
        }
        catch (SqlException ex)
        {
            throw SqlExceptionTranslator.TryTranslate(ex, "Readers.GetByCardNumber") ?? (Exception)ex;
        }
    }

    public async Task<IReadOnlyList<Reader>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM   dbo.Readers
            WHERE  IsDeleted = 0
            ORDER BY Id
            """;

        await EnsureOpenAsync(ct);
        await using var command = new SqlCommand(sql, Connection, Transaction);

        var result = new List<Reader>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(Map(reader));
        }
        catch (SqlException ex)
        {
            throw SqlExceptionTranslator.TryTranslate(ex, "Readers.GetAll") ?? (Exception)ex;
        }
        return result;
    }

    public async Task<long> AddAsync(Reader reader, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.Readers (CardNumber, Email, FullName, Phone, IsBlocked, CreatedBy)
            VALUES (@CardNumber, @Email, @FullName, @Phone, @IsBlocked, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """;

        await EnsureOpenAsync(ct);
        await using var command = new SqlCommand(sql, Connection, Transaction);
        command.Parameters.Add("@CardNumber", SqlDbType.NVarChar, 20).Value  = reader.CardNumber;
        command.Parameters.Add("@Email",      SqlDbType.NVarChar, 256).Value = reader.Email;
        command.Parameters.Add("@FullName",   SqlDbType.NVarChar, 200).Value = reader.FullName;
        command.Parameters.Add("@Phone",      SqlDbType.NVarChar, 30).Value  = (object?)reader.Phone ?? DBNull.Value;
        command.Parameters.Add("@IsBlocked",  SqlDbType.Bit).Value           = reader.IsBlocked;
        command.Parameters.Add("@CreatedBy",  SqlDbType.NVarChar, 100).Value = (object?)reader.CreatedBy ?? DBNull.Value;

        try
        {
            var scalar = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt64(scalar);
        }
        catch (SqlException ex)
        {
            throw SqlExceptionTranslator.TryTranslate(ex, "Readers.Add") ?? (Exception)ex;
        }
    }

    public async Task<bool> SetBlockedAsync(long id, bool isBlocked, string? changedBy, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.Readers
            SET    IsBlocked = @IsBlocked,
                   UpdatedAt = SYSUTCDATETIME(),
                   UpdatedBy = @UpdatedBy
            WHERE  Id = @Id AND IsDeleted = 0
            """;

        await EnsureOpenAsync(ct);
        await using var command = new SqlCommand(sql, Connection, Transaction);
        command.Parameters.Add("@Id",        SqlDbType.BigInt).Value          = id;
        command.Parameters.Add("@IsBlocked", SqlDbType.Bit).Value             = isBlocked;
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value   = (object?)changedBy ?? DBNull.Value;

        try
        {
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqlException ex)
        {
            throw SqlExceptionTranslator.TryTranslate(ex, "Readers.SetBlocked") ?? (Exception)ex;
        }
    }

    public async Task<bool> SoftDeleteAsync(long id, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.Readers
            SET    IsDeleted = 1, UpdatedAt = SYSUTCDATETIME()
            WHERE  Id = @Id AND IsDeleted = 0
            """;

        await EnsureOpenAsync(ct);
        await using var command = new SqlCommand(sql, Connection, Transaction);
        command.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;

        try
        {
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqlException ex)
        {
            throw SqlExceptionTranslator.TryTranslate(ex, "Readers.SoftDelete") ?? (Exception)ex;
        }
    }

    public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
    {
        const string sql = "SELECT 1 FROM dbo.Readers WHERE Email = @Email AND IsDeleted = 0";

        await EnsureOpenAsync(ct);
        await using var command = new SqlCommand(sql, Connection, Transaction);
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 256).Value = email;

        try
        {
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        catch (SqlException ex)
        {
            throw SqlExceptionTranslator.TryTranslate(ex, "Readers.ExistsByEmail") ?? (Exception)ex;
        }
    }

    /// <summary>
    /// Ручний мапінг рядка на модель домену — те, що Dapper робить автоматично.
    /// Індекси стовпців відповідають порядку в <see cref="SelectColumns"/>.
    /// </summary>
    private static Reader Map(SqlDataReader r) => new()
    {
        Id         = r.GetInt64(0),
        CardNumber = r.GetString(1),
        Email      = r.GetString(2),
        FullName   = r.GetString(3),
        Phone      = r.IsDBNull(4) ? null : r.GetString(4),
        IsBlocked  = r.GetBoolean(5),
        CreatedAt  = r.GetDateTime(6),
        CreatedBy  = r.IsDBNull(7) ? null : r.GetString(7),
        UpdatedAt  = r.IsDBNull(8) ? null : r.GetDateTime(8),
        UpdatedBy  = r.IsDBNull(9) ? null : r.GetString(9),
        IsDeleted  = r.GetBoolean(10)
    };
}
