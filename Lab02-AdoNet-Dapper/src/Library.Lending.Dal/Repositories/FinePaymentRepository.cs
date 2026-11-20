using Dapper;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Dal.Infrastructure;
using Library.Lending.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Repositories;

/// <summary>
/// Репозиторій оплат пені — <b>ADO.NET + Dapper</b> (третя Dapper-реалізація).
/// Демонструє агрегатний запит і роботу з токеном ідемпотентності.
/// </summary>
public sealed class FinePaymentRepository : BaseDapperRepository<FinePayment>, IFinePaymentRepository
{
    public FinePaymentRepository(SqlConnection connection, Func<SqlTransaction?> currentTransaction)
        : base(connection, currentTransaction, "dbo.FinePayments") { }

    protected override string SelectColumns =>
        "Id, LoanId, Amount, Method, PaidAt, IdempotencyKey";

    public async Task<IReadOnlyList<FinePayment>> GetByLoanAsync(long loanId, CancellationToken ct = default)
    {
        // індекс IX_FinePayments_LoanId обслуговує саме цей запит
        const string sql = """
            SELECT  Id, LoanId, Amount, Method, PaidAt, IdempotencyKey
            FROM    dbo.FinePayments
            WHERE   LoanId = @LoanId
            ORDER BY PaidAt DESC, Id DESC
            """;

        await EnsureOpenAsync(ct);
        var rows = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QueryAsync<FinePayment>(Command(sql, new { LoanId = loanId }, ct)),
            "FinePayments.GetByLoan");
        return rows.ToList();
    }

    /// <summary>Агрегатний запит: суму рахує БД, а не застосунок.</summary>
    public async Task<decimal> GetTotalPaidAsync(long loanId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT ISNULL(SUM(Amount), 0)
            FROM   dbo.FinePayments
            WHERE  LoanId = @LoanId
            """;

        await EnsureOpenAsync(ct);
        return await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteScalarAsync<decimal>(Command(sql, new { LoanId = loanId }, ct)),
            "FinePayments.GetTotalPaid");
    }

    /// <summary>
    /// Пошук за токеном ідемпотентності: якщо платіж уже існує, повторна
    /// відправка запиту НЕ створює другий платіж, а повертає наявний.
    /// </summary>
    public async Task<FinePayment?> FindByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        const string sql = """
            SELECT  Id, LoanId, Amount, Method, PaidAt, IdempotencyKey
            FROM    dbo.FinePayments
            WHERE   IdempotencyKey = @Key
            """;

        await EnsureOpenAsync(ct);
        return await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QuerySingleOrDefaultAsync<FinePayment>(Command(sql, new { Key = key }, ct)),
            "FinePayments.FindByIdempotencyKey");
    }

    public override async Task<long> AddAsync(FinePayment entity, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.FinePayments (LoanId, Amount, Method, IdempotencyKey)
            VALUES (@LoanId, @Amount, @Method, @IdempotencyKey);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """;

        await EnsureOpenAsync(ct);
        return await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteScalarAsync<long>(Command(sql, new
            {
                entity.LoanId, entity.Amount, entity.Method, entity.IdempotencyKey
            }, ct)),
            "FinePayments.Add");
    }

    public override async Task<bool> UpdateAsync(FinePayment entity, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.FinePayments
            SET    Amount = @Amount, Method = @Method
            WHERE  Id = @Id
            """;

        await EnsureOpenAsync(ct);
        var affected = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(sql, new { entity.Id, entity.Amount, entity.Method }, ct)),
            "FinePayments.Update");
        return affected > 0;
    }
}
