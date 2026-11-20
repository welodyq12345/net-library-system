using System.Data;
using Dapper;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Dal.Infrastructure;
using Library.Lending.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Dal.Repositories;

/// <summary>
/// Репозиторій локальної копії довідника книг — <b>ADO.NET + Dapper</b>.
/// </summary>
/// <remarks>
/// <see cref="DecreaseCopiesAsync"/> — це ДРУГИЙ репозиторій транзакційного
/// сценарію «підтвердити й видати»: якщо тут виникне помилка (брак
/// примірників), зміни першого репозиторію (статус видачі) відкотяться
/// разом із усією транзакцією Unit of Work.
/// </remarks>
public sealed class BookRepository : BaseDapperRepository<Book>, IBookRepository
{
    public BookRepository(SqlConnection connection, Func<SqlTransaction?> currentTransaction)
        : base(connection, currentTransaction, "dbo.Books") { }

    protected override string SelectColumns =>
        "Id, Title, Author, Isbn, DailyFee, CopiesAvailable, SyncedAt";

    public async Task<IReadOnlyList<Book>> SearchAsync(string? title, CancellationToken ct = default)
    {
        // індекс IX_Books_Title обслуговує саме цей запит.
        // Значення передається ПАРАМЕТРОМ — ніякої конкатенації з вводом.
        const string sql = """
            SELECT  Id, Title, Author, Isbn, DailyFee, CopiesAvailable, SyncedAt
            FROM    dbo.Books
            WHERE   @Title IS NULL OR Title LIKE '%' + @Title + '%'
            ORDER BY Title
            """;

        await EnsureOpenAsync(ct);
        var rows = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.QueryAsync<Book>(Command(sql, new { Title = title }, ct)),
            "Books.Search");
        return rows.ToList();
    }

    /// <summary>Зменшення залишку через <c>usp_DecreaseBookCopies</c> (перевірка + UPDLOCK у БД).</summary>
    public async Task DecreaseCopiesAsync(long bookId, int quantity, CancellationToken ct = default)
    {
        await EnsureOpenAsync(ct);
        await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(
                "dbo.usp_DecreaseBookCopies",
                new { BookId = bookId, Quantity = quantity },
                ct, CommandType.StoredProcedure)),
            "Books.DecreaseCopies");
    }

    public async Task IncreaseCopiesAsync(long bookId, int quantity, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.Books
            SET    CopiesAvailable = CopiesAvailable + @Quantity
            WHERE  Id = @BookId
            """;

        await EnsureOpenAsync(ct);
        await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(sql, new { BookId = bookId, Quantity = quantity }, ct)),
            "Books.IncreaseCopies");
    }

    /// <summary>
    /// Синхронізація копії довідника з Каталогу.
    /// <b>CopiesAvailable свідомо НЕ перетирається</b>: це власні дані контексту
    /// Видачі, ними керують операції видачі/повернення (таблиця політики
    /// дублювання, Лаб. 01, рядок 3).
    /// </summary>
    public async Task UpsertFromCatalogAsync(Book book, CancellationToken ct = default)
    {
        const string sql = """
            MERGE dbo.Books AS target
            USING (SELECT @Id AS Id, @Title AS Title, @Author AS Author,
                          @Isbn AS Isbn, @DailyFee AS DailyFee) AS src
            ON target.Id = src.Id
            WHEN MATCHED THEN
                UPDATE SET Title = src.Title, Author = src.Author, Isbn = src.Isbn,
                           DailyFee = src.DailyFee, SyncedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (Id, Title, Author, Isbn, DailyFee, CopiesAvailable)
                VALUES (src.Id, src.Title, src.Author, src.Isbn, src.DailyFee, @InitialCopies);
            """;

        await EnsureOpenAsync(ct);
        await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(sql, new
            {
                book.Id, book.Title, book.Author, book.Isbn, book.DailyFee,
                InitialCopies = book.CopiesAvailable
            }, ct)),
            "Books.UpsertFromCatalog");
    }

    public override async Task<long> AddAsync(Book entity, CancellationToken ct = default)
    {
        // Id НЕ генерується тут: він приходить із БД Каталогу (власника).
        const string sql = """
            INSERT INTO dbo.Books (Id, Title, Author, Isbn, DailyFee, CopiesAvailable)
            VALUES (@Id, @Title, @Author, @Isbn, @DailyFee, @CopiesAvailable);
            SELECT @Id;
            """;

        await EnsureOpenAsync(ct);
        return await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteScalarAsync<long>(Command(sql, new
            {
                entity.Id, entity.Title, entity.Author, entity.Isbn,
                entity.DailyFee, entity.CopiesAvailable
            }, ct)),
            "Books.Add");
    }

    public override async Task<bool> UpdateAsync(Book entity, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.Books
            SET    Title = @Title, Author = @Author, Isbn = @Isbn,
                   DailyFee = @DailyFee, SyncedAt = SYSUTCDATETIME()
            WHERE  Id = @Id
            """;

        await EnsureOpenAsync(ct);
        var affected = await SqlExceptionTranslator.ExecuteAsync(
            () => Connection.ExecuteAsync(Command(sql, new
            {
                entity.Id, entity.Title, entity.Author, entity.Isbn, entity.DailyFee
            }, ct)),
            "Books.Update");
        return affected > 0;
    }
}
