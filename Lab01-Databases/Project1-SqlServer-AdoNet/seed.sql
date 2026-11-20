/* =============================================================================
   ПРОЄКТ №1 — LibraryLendingDb : тестові дані
   -----------------------------------------------------------------------------
   ІДЕМПОТЕНТНІСТЬ: кожна вставка захищена перевіркою наявності (IF NOT EXISTS /
   MERGE-подібний патерн), тому ПОВТОРНИЙ запуск цього скрипта НЕ створює
   дублікатів. Перевірка — у verify.sql (блок «ідемпотентність»).
   ============================================================================= */

USE LibraryLendingDb;
GO

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------------------------------------------------------------------------
   1. Читачі (власні дані контексту)
   --------------------------------------------------------------------------- */
INSERT INTO dbo.Readers (CardNumber, Email, FullName, Phone, CreatedBy)
SELECT v.CardNumber, v.Email, v.FullName, v.Phone, 'seed'
FROM (VALUES
        (N'LC-000001', N'olena.k@example.com',  N'Олена Коваленко', N'+380501112233'),
        (N'LC-000002', N'andrii.sh@example.com', N'Андрій Шевченко', N'+380502223344'),
        (N'LC-000003', N'maria.b@example.com',   N'Марія Бондаренко', N'+380503334455'),
        (N'LC-000004', N'obrien@example.com',    N'Liam O''Brien',    N'+380504445566')  -- спецсимвол: перевірка проти SQL-ін'єкцій
     ) AS v (CardNumber, Email, FullName, Phone)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Readers r WHERE r.CardNumber = v.CardNumber);
GO

/* ---------------------------------------------------------------------------
   2. Локальна копія довідника книг (Id — ті самі, що в LibraryCatalogDb)
      Джерело даних: Book у Проєкті №2. Оновлюється подіями з Каталогу.
   --------------------------------------------------------------------------- */
MERGE dbo.Books AS target
USING (VALUES
        (1, N'Кобзар',                      N'Тарас Шевченко',     N'978-966-03-5555-1', CAST(3.50 AS DECIMAL(18,2)), 5),
        (2, N'Тигролови',                   N'Іван Багряний',      N'978-966-03-5555-2', CAST(4.00 AS DECIMAL(18,2)), 3),
        (3, N'Місто',                       N'Валер''ян Підмогильний', N'978-966-03-5555-3', CAST(4.25 AS DECIMAL(18,2)), 4),
        (4, N'Інтернат',                    N'Сергій Жадан',       N'978-966-03-5555-4', CAST(5.00 AS DECIMAL(18,2)), 2),
        (5, N'Чорний Ворон',                N'Василь Шкляр',       N'978-966-03-5555-5', CAST(4.75 AS DECIMAL(18,2)), 6),
        (6, N'Записки українського самашедшого', N'Ліна Костенко',  N'978-966-03-5555-6', CAST(5.50 AS DECIMAL(18,2)), 1)
      ) AS src (Id, Title, Author, Isbn, DailyFee, CopiesAvailable)
ON target.Id = src.Id
WHEN MATCHED THEN
    UPDATE SET Title = src.Title, Author = src.Author, Isbn = src.Isbn,
               DailyFee = src.DailyFee, SyncedAt = SYSUTCDATETIME()
               -- CopiesAvailable НЕ перетираємо: ним керують видачі/повернення
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Id, Title, Author, Isbn, DailyFee, CopiesAvailable)
    VALUES (src.Id, src.Title, src.Author, src.Isbn, src.DailyFee, src.CopiesAvailable);
GO

/* ---------------------------------------------------------------------------
   3. Сценарні видачі — створюємо через процедуру, щоб відпрацювали
      знімки полів каталогу та журнал статусів.
      Ідемпотентність: вставляємо лише якщо в читача ще немає жодної видачі.
   --------------------------------------------------------------------------- */
DECLARE @ReaderOlena  BIGINT = (SELECT Id FROM dbo.Readers WHERE CardNumber = N'LC-000001');
DECLARE @ReaderAndrii BIGINT = (SELECT Id FROM dbo.Readers WHERE CardNumber = N'LC-000002');
DECLARE @ReaderMaria  BIGINT = (SELECT Id FROM dbo.Readers WHERE CardNumber = N'LC-000003');
DECLARE @LoanId BIGINT, @Fine DECIMAL(18,2);

/* --- Сценарій A: видача у статусі Submitted (нова) --- */
IF NOT EXISTS (SELECT 1 FROM dbo.Loans WHERE ReaderId = @ReaderOlena)
BEGIN
    EXEC dbo.usp_CreateLoan
        @ReaderId     = @ReaderOlena,
        @DueDate      = '2026-12-20',
        @PickupBranch = N'Центральна філія',
        @Comment      = N'Перша видача читача',
        @ItemsJson    = N'[{"BookId":1,"Units":1},{"BookId":2,"Units":1}]',
        @CreatedBy    = N'seed',
        @LoanId       = @LoanId OUTPUT;
END

/* --- Сценарій B: видача на руках (Issued), ТЕРМІН ЯКОЇ ВЖЕ ВИЙШОВ.
       LoanDate задаємо явно в минулому, щоб DueDate < сьогодні не порушив
       обмеження CK_Loans_DueAfterLoan (DueDate >= LoanDate).              --- */
IF NOT EXISTS (SELECT 1 FROM dbo.Loans WHERE ReaderId = @ReaderAndrii)
BEGIN
    EXEC dbo.usp_CreateLoan
        @ReaderId     = @ReaderAndrii,
        @LoanDate     = '2026-10-20T10:00:00',
        @DueDate      = '2026-11-10',             -- термін вийшов -> прострочена
        @PickupBranch = N'Філія №2',
        @Comment      = N'Видача для демонстрації пені',
        @ItemsJson    = N'[{"BookId":5,"Units":2}]',
        @CreatedBy    = N'seed',
        @LoanId       = @LoanId OUTPUT;

    EXEC dbo.usp_ConfirmLoanCopies @LoanId = @LoanId, @ChangedBy = N'seed';
    EXEC dbo.usp_IssueLoan        @LoanId = @LoanId, @ChangedBy = N'seed';
END

/* --- Сценарій C: повний цикл до Returned з нарахованою пенею
       (повернуто 2026-11-08 при терміні 2026-11-01 => 7 днів прострочення) --- */
IF NOT EXISTS (SELECT 1 FROM dbo.Loans WHERE ReaderId = @ReaderMaria)
BEGIN
    EXEC dbo.usp_CreateLoan
        @ReaderId     = @ReaderMaria,
        @LoanDate     = '2026-10-15T09:30:00',
        @DueDate      = '2026-11-01',
        @PickupBranch = N'Центральна філія',
        @Comment      = N'Завершений цикл видачі',
        @ItemsJson    = N'[{"BookId":3,"Units":1},{"BookId":4,"Units":1}]',
        @CreatedBy    = N'seed',
        @LoanId       = @LoanId OUTPUT;

    EXEC dbo.usp_ConfirmLoanCopies @LoanId = @LoanId, @ChangedBy = N'seed';
    EXEC dbo.usp_IssueLoan        @LoanId = @LoanId, @ChangedBy = N'seed';
    EXEC dbo.usp_ReturnLoan       @LoanId = @LoanId,
                                  @ReturnDate = '2026-11-08',
                                  @ChangedBy  = N'seed',
                                  @Fine       = @Fine OUTPUT;

    /* оплата нарахованої пені — з idempotency-ключем */
    IF @Fine > 0 AND NOT EXISTS (SELECT 1 FROM dbo.FinePayments WHERE IdempotencyKey = N'seed-fine-1')
        INSERT INTO dbo.FinePayments (LoanId, Amount, Method, IdempotencyKey)
        VALUES (@LoanId, @Fine, N'Card', N'seed-fine-1');
END
GO

PRINT N'LibraryLendingDb: seed.sql виконано успішно (повторний запуск дублікатів не створює).';
GO

SELECT 'Readers' AS [Table], COUNT(*) AS [Rows] FROM dbo.Readers
UNION ALL SELECT 'Books',             COUNT(*) FROM dbo.Books
UNION ALL SELECT 'Loans',             COUNT(*) FROM dbo.Loans
UNION ALL SELECT 'LoanDetails',       COUNT(*) FROM dbo.LoanDetails
UNION ALL SELECT 'LoanItems',         COUNT(*) FROM dbo.LoanItems
UNION ALL SELECT 'LoanStatusHistory', COUNT(*) FROM dbo.LoanStatusHistory
UNION ALL SELECT 'FinePayments',      COUNT(*) FROM dbo.FinePayments;
GO
