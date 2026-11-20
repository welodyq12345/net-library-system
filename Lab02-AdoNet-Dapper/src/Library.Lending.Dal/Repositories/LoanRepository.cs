using System.Data;
using System.Text.Json;
using Dapper;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Dal.Infrastructure;
using Library.Lending.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Repositories;

/// <summary>
/// Репозиторій видач на <b>ADO.NET + Dapper</b>.
/// </summary>
/// <remarks>
/// Демонструє:
/// <list type="bullet">
///   <item><b>multi-mapping</b> — видача з позиціями одним запитом (<c>GetWithItemsAsync</c>);</item>
///   <item><b>виклики збережуваних процедур</b> із Лаб. 01, у тому числі з OUTPUT-параметром;</item>
///   <item>успадкування спільного CRUD від <see cref="BaseDapperRepository{T}"/>.</item>
/// </list>
/// Усі методи асинхронні (<c>QueryAsync</c>, <c>ExecuteAsync</c>) і працюють
/// через з'єднання та транзакцію, які надає Unit of Work.
/// </remarks>
public sealed class LoanRepository : BaseDapperRepository<Loan>, ILoanRepository
{
    public LoanRepository(SqlConnection connection, Func<SqlTransaction?> currentTransaction)
        : base(connection, currentTransaction, "dbo.Loans") { }

    protected override string SelectColumns => """
        Id, ReaderId, Status, LoanDate, DueDate, IssuedAt, ReturnedAt, FineAmount,
        CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted
        """;

    protected override string NotDeletedFilter => "IsDeleted = 0";

    /// <summary>
    /// Видача разом із позиціями — ОДНИМ запитом із LEFT JOIN.
    /// Dapper multi-mapping: кожен рядок результату розбивається на
    /// <see cref="Loan"/> + один <see cref="LoanItem"/> починаючи зі стовпця,
    /// указаного в <c>splitOn</c>.
    /// </summary>
    public async Task<Loan?> GetWithItemsAsync(long loanId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT  l.Id, l.ReaderId, l.Status, l.LoanDate, l.DueDate,
                    l.IssuedAt, l.ReturnedAt, l.FineAmount, l.IsDeleted,
                    d.LoanId, d.PickupBranch, d.LibrarianName, d.Comment,
                    i.LoanId, i.BookId, i.BookTitle, i.BookAuthor,
                    i.DailyFee, i.Units, i.Discount
            FROM        dbo.Loans       AS l
            LEFT  JOIN  dbo.LoanDetails AS d ON d.LoanId = l.Id
            LEFT  JOIN  dbo.LoanItems   AS i ON i.LoanId = l.Id
            WHERE   l.Id = @LoanId AND l.IsDeleted = 0
            ORDER BY i.BookId
            """;

        await EnsureOpenAsync(ct);

        Loan? result = null;

        await SqlExceptionTranslator.ExecuteAsync(async () =>
        {
            await Connection.QueryAsync<Loan, LoanDetail?, LoanItem?, Loan>(
                Command(sql, new { LoanId = loanId }, ct),
                (loan, detail, item) =>
                {
                    // перший рядок визначає корінь агрегату
                    result ??= loan;
                    result.Detail ??= detail;

                    // LEFT JOIN дає NULL-позицію, якщо позицій немає взагалі
                    if (item is not null && result.Items.All(x => x.BookId != item.BookId))
                        result.Items.Add(item);

                    return result;
                },
                // з цих стовпців починаються LoanDetail і LoanItem відповідно
                splitOn: "LoanId,LoanId");
        }, "Loans.GetWithItems");

        return result;
    }

    public async Task<IReadOnlyList<Loan>> GetByReaderAsync(
        long readerId, string? status, CancellationToken ct = default)
    {
        // індекс IX_Loans_ReaderId_Status обслуговує саме цей запит
        const string sql = """
            SELECT  Id, ReaderId, Status, LoanDate, DueDate, IssuedAt, ReturnedAt,
                    FineAmount, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted
            FROM    dbo.Loans
            WHERE   ReaderId = @ReaderId
              AND   IsDeleted = 0
              AND  (@Status IS NULL OR Status = @Status)
            ORDER BY LoanDate DESC
            """;

        await EnsureOpenAsync(ct);
        var rows = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QueryAsync<Loan>(Command(sql, new { ReaderId = readerId, Status = status }, ct)),
            "Loans.GetByReader");
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Loan>> GetOverdueAsync(DateOnly today, CancellationToken ct = default)
    {
        // фільтрований індекс IX_Loans_Overdue обслуговує саме цей запит
        const string sql = """
            SELECT  Id, ReaderId, Status, LoanDate, DueDate, IssuedAt, ReturnedAt,
                    FineAmount, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted
            FROM    dbo.Loans
            WHERE   Status = 'Issued' AND IsDeleted = 0 AND DueDate < @Today
            ORDER BY DueDate
            """;

        await EnsureOpenAsync(ct);
        var rows = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QueryAsync<Loan>(Command(sql, new { Today = today.ToDateTime(TimeOnly.MinValue) }, ct)),
            "Loans.GetOverdue");
        return rows.ToList();
    }

    /// <summary>
    /// Створення видачі через <c>usp_CreateLoan</c>: процедура в одній
    /// транзакції створює видачу, деталі та позиції зі ЗНІМКАМИ полів каталогу.
    /// Позиції передаються як JSON-масив (у процедурі — <c>OPENJSON</c>).
    /// </summary>
    public async Task<long> CreateViaProcedureAsync(
        long readerId, DateOnly dueDate, string pickupBranch, string? comment,
        IEnumerable<(long BookId, int Units)> items, string? createdBy,
        CancellationToken ct = default)
    {
        var itemsJson = JsonSerializer.Serialize(
            items.Select(i => new { BookId = i.BookId, Units = i.Units }));

        var parameters = new DynamicParameters();
        parameters.Add("@ReaderId",     readerId,     DbType.Int64);
        parameters.Add("@DueDate",      dueDate.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("@PickupBranch", pickupBranch, DbType.String, size: 100);
        parameters.Add("@Comment",      comment,      DbType.String, size: 1000);
        parameters.Add("@ItemsJson",    itemsJson,    DbType.String);
        parameters.Add("@CreatedBy",    createdBy,    DbType.String, size: 100);
        parameters.Add("@LoanDate",     null,         DbType.DateTime2);
        parameters.Add("@LoanId",       dbType: DbType.Int64, direction: ParameterDirection.Output);

        await EnsureOpenAsync(ct);
        await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(
                "dbo.usp_CreateLoan", parameters, ct, CommandType.StoredProcedure)),
            "Loans.CreateViaProcedure");

        return parameters.Get<long>("@LoanId");
    }

    public Task ConfirmCopiesAsync(long loanId, string? changedBy, CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.usp_ConfirmLoanCopies",
            new { LoanId = loanId, ChangedBy = changedBy }, "Loans.ConfirmCopies", ct);

    public Task IssueAsync(long loanId, string? changedBy, CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.usp_IssueLoan",
            new { LoanId = loanId, ChangedBy = changedBy }, "Loans.Issue", ct);

    /// <summary>
    /// <c>usp_ReturnLoan</c> з OUTPUT-параметром: процедура сама рахує пеню
    /// за прострочені дні, використовуючи ЗНІМОК тарифу з позицій видачі.
    /// </summary>
    public async Task<decimal> ReturnAsync(
        long loanId, DateTime? returnDate, string? changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@LoanId",     loanId,     DbType.Int64);
        parameters.Add("@ReturnDate", returnDate, DbType.DateTime2);
        parameters.Add("@ChangedBy",  changedBy,  DbType.String, size: 100);
        parameters.Add("@Fine", dbType: DbType.Decimal, direction: ParameterDirection.Output,
                       precision: 18, scale: 2);

        await EnsureOpenAsync(ct);
        await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(
                "dbo.usp_ReturnLoan", parameters, ct, CommandType.StoredProcedure)),
            "Loans.Return");

        return parameters.Get<decimal>("@Fine");
    }

    public Task CancelAsync(long loanId, string? reason, string? changedBy, CancellationToken ct = default) =>
        ExecuteProcedureAsync("dbo.usp_CancelLoan",
            new { LoanId = loanId, Reason = reason, ChangedBy = changedBy }, "Loans.Cancel", ct);

    public async Task<IReadOnlyList<LoanSummary>> GetReaderSummaryAsync(
        long readerId, string? status, CancellationToken ct = default)
    {
        await EnsureOpenAsync(ct);
        var rows = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QueryAsync<LoanSummary>(Command(
                "dbo.usp_GetReaderLoanSummary",
                new { ReaderId = readerId, Status = status },
                ct, CommandType.StoredProcedure)),
            "Loans.GetReaderSummary");
        return rows.ToList();
    }

    public async Task<IReadOnlyList<LoanStatusHistoryEntry>> GetStatusHistoryAsync(
        long loanId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT  Id, LoanId, FromStatus, ToStatus, ChangedAt, ChangedBy, Note
            FROM    dbo.LoanStatusHistory
            WHERE   LoanId = @LoanId
            ORDER BY ChangedAt, Id
            """;

        await EnsureOpenAsync(ct);
        var rows = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QueryAsync<LoanStatusHistoryEntry>(Command(sql, new { LoanId = loanId }, ct)),
            "Loans.GetStatusHistory");
        return rows.ToList();
    }

    /// <summary>Пряма вставка (без процедури) — реалізація контракту Generic Repository.</summary>
    public override async Task<long> AddAsync(Loan entity, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.Loans (ReaderId, Status, LoanDate, DueDate, CreatedBy)
            VALUES (@ReaderId, @Status, @LoanDate, @DueDate, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """;

        await EnsureOpenAsync(ct);
        return await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteScalarAsync<long>(Command(sql, new
            {
                entity.ReaderId,
                entity.Status,
                LoanDate = entity.LoanDate == default ? DateTime.UtcNow : entity.LoanDate,
                DueDate  = entity.DueDate.ToDateTime(TimeOnly.MinValue),
                entity.CreatedBy
            }, ct)),
            "Loans.Add");
    }

    public override async Task<bool> UpdateAsync(Loan entity, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.Loans
            SET    DueDate   = @DueDate,
                   UpdatedAt = SYSUTCDATETIME(),
                   UpdatedBy = @UpdatedBy
            WHERE  Id = @Id AND IsDeleted = 0
            """;

        await EnsureOpenAsync(ct);
        var affected = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(sql, new
            {
                entity.Id,
                DueDate   = entity.DueDate.ToDateTime(TimeOnly.MinValue),
                UpdatedBy = entity.UpdatedBy
            }, ct)),
            "Loans.Update");
        return affected > 0;
    }

    private async Task ExecuteProcedureAsync(
        string procedure, object parameters, string context, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(procedure, parameters, ct, CommandType.StoredProcedure)),
            context);
    }
}
