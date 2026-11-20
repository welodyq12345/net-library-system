/* =============================================================================
   ПРОЄКТ №1 — LibraryLendingDb : збережувані процедури
   -----------------------------------------------------------------------------
   7 процедур: CRUD + бізнес-операції з транзакційною логікою.
   Коди помилок (їх перехоплює DAL у Лаб. 02 і перетворює на HTTP-статуси):
     50001 — сутність не знайдено            -> NotFoundException      -> 404
     50002 — недопустимий перехід статусу    -> BusinessConflictException -> 409
     50003 — недостатньо примірників         -> BusinessConflictException -> 409
     50004 — порушення бізнес-правила        -> BusinessConflictException -> 409
   -----------------------------------------------------------------------------
   Допустимі переходи статусів:
     Submitted -> AwaitingValidation -> CopiesConfirmed -> Issued -> Returned
     Cancelled — лише з Submitted / AwaitingValidation / CopiesConfirmed
   ============================================================================= */

USE LibraryLendingDb;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* =============================================================================
   1. usp_CreateLoan — створення видачі разом із позиціями в ОДНІЙ транзакції.
      Назва, автор і тариф книги беруться з dbo.Books і записуються в позицію
      як ЗНІМОК — за таблицею політики дублювання (Лаб. 01, Крок 2).
      @ItemsJson: [{"BookId":1,"Units":2},{"BookId":5,"Units":1}]
   ============================================================================= */
CREATE OR ALTER PROCEDURE dbo.usp_CreateLoan
    @ReaderId     BIGINT,
    @DueDate      DATE,
    @PickupBranch NVARCHAR(100),
    @Comment      NVARCHAR(1000) = NULL,
    @ItemsJson    NVARCHAR(MAX),
    @CreatedBy    NVARCHAR(100)  = NULL,
    @LoanDate     DATETIME2(3)   = NULL,   -- NULL => SYSUTCDATETIME(); задається
                                           -- явно лише для seed / міграції даних
    @LoanId       BIGINT         OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;                 -- будь-яка помилка відкочує транзакцію

    SET @LoanDate = ISNULL(@LoanDate, SYSUTCDATETIME());

    IF @DueDate < CAST(@LoanDate AS DATE)
        THROW 50004, 'DueDate cannot be earlier than LoanDate', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Readers WHERE Id = @ReaderId AND IsDeleted = 0)
        THROW 50001, 'Reader not found', 1;

    IF EXISTS (SELECT 1 FROM dbo.Readers WHERE Id = @ReaderId AND IsBlocked = 1)
        THROW 50004, 'Reader is blocked and cannot borrow books', 1;

    IF ISJSON(@ItemsJson) <> 1
        THROW 50004, 'ItemsJson is not a valid JSON array', 1;

    BEGIN TRANSACTION;

    /* розбираємо вхідний JSON у тимчасову таблицю */
    DECLARE @Items TABLE (BookId BIGINT PRIMARY KEY, Units INT NOT NULL);

    INSERT INTO @Items (BookId, Units)
    SELECT  j.BookId, SUM(j.Units)
    FROM    OPENJSON(@ItemsJson)
            WITH (BookId BIGINT '$.BookId', Units INT '$.Units') AS j
    GROUP BY j.BookId;

    IF NOT EXISTS (SELECT 1 FROM @Items)
        THROW 50004, 'Loan must contain at least one item', 1;

    IF EXISTS (SELECT 1 FROM @Items WHERE Units <= 0)
        THROW 50004, 'Units must be positive', 1;

    /* усі книги мають існувати в локальній копії довідника */
    IF EXISTS (SELECT 1 FROM @Items i
               WHERE NOT EXISTS (SELECT 1 FROM dbo.Books b WHERE b.Id = i.BookId))
        THROW 50001, 'One or more books not found', 1;

    INSERT INTO dbo.Loans (ReaderId, Status, LoanDate, DueDate, CreatedBy)
    VALUES (@ReaderId, 'Submitted', @LoanDate, @DueDate, @CreatedBy);

    SET @LoanId = CAST(SCOPE_IDENTITY() AS BIGINT);

    INSERT INTO dbo.LoanDetails (LoanId, PickupBranch, LibrarianName, Comment)
    VALUES (@LoanId, @PickupBranch, @CreatedBy, @Comment);

    /* ЗНІМОК полів каталогу на момент оформлення */
    INSERT INTO dbo.LoanItems (LoanId, BookId, BookTitle, BookAuthor, DailyFee, Units)
    SELECT  @LoanId, b.Id, b.Title, b.Author, b.DailyFee, i.Units
    FROM    @Items AS i
    INNER JOIN dbo.Books AS b ON b.Id = i.BookId;

    INSERT INTO dbo.LoanStatusHistory (LoanId, FromStatus, ToStatus, ChangedBy, Note)
    VALUES (@LoanId, NULL, 'Submitted', @CreatedBy, 'Loan created');

    COMMIT TRANSACTION;
END
GO

/* =============================================================================
   2. usp_ConfirmLoanCopies — НАЙВАЖЛИВІША транзакційна процедура.
      Перехід Submitted / AwaitingValidation -> CopiesConfirmed.
      Працює в явній транзакції та ВІДМОВЛЯЄ керованою помилкою, якщо
      перехід недопустимий (напр. видачу вже видано на руки або скасовано).
      UPDLOCK блокує рядок до кінця транзакції — два паралельні підтвердження
      не зможуть «тихо» перетерти одне одного.
   ============================================================================= */
CREATE OR ALTER PROCEDURE dbo.usp_ConfirmLoanCopies
    @LoanId    BIGINT,
    @ChangedBy NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @Status NVARCHAR(30) =
        (SELECT Status FROM dbo.Loans WITH (UPDLOCK) WHERE Id = @LoanId AND IsDeleted = 0);

    IF @Status IS NULL
        THROW 50001, 'Loan not found', 1;

    IF @Status NOT IN ('Submitted', 'AwaitingValidation')
        THROW 50002, 'Copies can be confirmed only for a new loan', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.LoanItems WHERE LoanId = @LoanId)
        THROW 50004, 'Loan has no items', 1;

    /* перевіряємо наявність примірників ДО переходу статусу */
    IF EXISTS (SELECT 1
               FROM   dbo.LoanItems AS i
               INNER JOIN dbo.Books AS b ON b.Id = i.BookId
               WHERE  i.LoanId = @LoanId AND b.CopiesAvailable < i.Units)
        THROW 50003, 'Not enough copies available for one or more books', 1;

    UPDATE dbo.Loans
    SET    Status    = 'CopiesConfirmed',
           UpdatedAt = SYSUTCDATETIME(),
           UpdatedBy = @ChangedBy
    WHERE  Id = @LoanId;

    INSERT INTO dbo.LoanStatusHistory (LoanId, FromStatus, ToStatus, ChangedBy, Note)
    VALUES (@LoanId, @Status, 'CopiesConfirmed', @ChangedBy, 'Copies availability confirmed');

    COMMIT TRANSACTION;
END
GO

/* =============================================================================
   3. usp_IssueLoan — видача книг на руки: CopiesConfirmed -> Issued.
      Зменшує залишок примірників у ЛОКАЛЬНІЙ копії довідника в тій самій
      транзакції, що й зміна статусу: або обидві дії, або жодна.
   ============================================================================= */
CREATE OR ALTER PROCEDURE dbo.usp_IssueLoan
    @LoanId    BIGINT,
    @ChangedBy NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @Status NVARCHAR(30) =
        (SELECT Status FROM dbo.Loans WITH (UPDLOCK) WHERE Id = @LoanId AND IsDeleted = 0);

    IF @Status IS NULL
        THROW 50001, 'Loan not found', 1;

    IF @Status <> 'CopiesConfirmed'
        THROW 50002, 'Only a loan with confirmed copies can be issued', 1;

    /* блокуємо рядки книг у детермінованому порядку (BookId) — проти deadlock */
    DECLARE @Shortage INT =
    (
        SELECT COUNT(*)
        FROM   dbo.LoanItems AS i
        INNER JOIN dbo.Books AS b WITH (UPDLOCK) ON b.Id = i.BookId
        WHERE  i.LoanId = @LoanId AND b.CopiesAvailable < i.Units
    );

    IF @Shortage > 0
        THROW 50003, 'Not enough copies available to issue the loan', 1;

    UPDATE  b
    SET     b.CopiesAvailable = b.CopiesAvailable - i.Units
    FROM    dbo.Books    AS b
    INNER JOIN dbo.LoanItems AS i ON i.BookId = b.Id
    WHERE   i.LoanId = @LoanId;

    UPDATE dbo.Loans
    SET    Status    = 'Issued',
           IssuedAt  = SYSUTCDATETIME(),
           UpdatedAt = SYSUTCDATETIME(),
           UpdatedBy = @ChangedBy
    WHERE  Id = @LoanId;

    INSERT INTO dbo.LoanStatusHistory (LoanId, FromStatus, ToStatus, ChangedBy, Note)
    VALUES (@LoanId, @Status, 'Issued', @ChangedBy, 'Books handed over to the reader');

    COMMIT TRANSACTION;
END
GO

/* =============================================================================
   4. usp_ReturnLoan — повернення: Issued -> Returned.
      Повертає примірники в довідник, рахує пеню за прострочені дні
      (тариф DailyFee — знімок із позиції видачі) — усе в одній транзакції.
   ============================================================================= */
CREATE OR ALTER PROCEDURE dbo.usp_ReturnLoan
    @LoanId     BIGINT,
    @ReturnDate DATETIME2(3)  = NULL,
    @ChangedBy  NVARCHAR(100) = NULL,
    @Fine       DECIMAL(18,2) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ReturnDate = ISNULL(@ReturnDate, SYSUTCDATETIME());

    BEGIN TRANSACTION;

    DECLARE @Status  NVARCHAR(30),
            @DueDate DATE;

    SELECT @Status = Status, @DueDate = DueDate
    FROM   dbo.Loans WITH (UPDLOCK)
    WHERE  Id = @LoanId AND IsDeleted = 0;

    IF @Status IS NULL
        THROW 50001, 'Loan not found', 1;

    IF @Status <> 'Issued'
        THROW 50002, 'Only an issued loan can be returned', 1;

    /* повертаємо примірники */
    UPDATE  b
    SET     b.CopiesAvailable = b.CopiesAvailable + i.Units
    FROM    dbo.Books    AS b
    INNER JOIN dbo.LoanItems AS i ON i.BookId = b.Id
    WHERE   i.LoanId = @LoanId;

    /* пеня = прострочені дні * сума денних тарифів позицій */
    DECLARE @OverdueDays INT = CASE
        WHEN CAST(@ReturnDate AS DATE) > @DueDate
        THEN DATEDIFF(DAY, @DueDate, CAST(@ReturnDate AS DATE))
        ELSE 0 END;

    DECLARE @DailyTotal DECIMAL(18,2) =
        ISNULL((SELECT SUM(i.DailyFee * i.Units - i.Discount)
                FROM dbo.LoanItems AS i WHERE i.LoanId = @LoanId), 0);

    SET @Fine = CAST(@OverdueDays * @DailyTotal AS DECIMAL(18,2));

    UPDATE dbo.Loans
    SET    Status     = 'Returned',
           ReturnedAt = @ReturnDate,
           FineAmount = @Fine,
           UpdatedAt  = SYSUTCDATETIME(),
           UpdatedBy  = @ChangedBy
    WHERE  Id = @LoanId;

    INSERT INTO dbo.LoanStatusHistory (LoanId, FromStatus, ToStatus, ChangedBy, Note)
    VALUES (@LoanId, @Status, 'Returned', @ChangedBy,
            CONCAT('Returned; overdue days: ', @OverdueDays, '; fine: ', @Fine));

    COMMIT TRANSACTION;
END
GO

/* =============================================================================
   5. usp_CancelLoan — скасування. Допустиме ЛИШЕ до видачі на руки.
      Демонструє відмову на недопустимому переході керованою помилкою.
   ============================================================================= */
CREATE OR ALTER PROCEDURE dbo.usp_CancelLoan
    @LoanId    BIGINT,
    @Reason    NVARCHAR(400)  = NULL,
    @ChangedBy NVARCHAR(100)  = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    DECLARE @Status NVARCHAR(30) =
        (SELECT Status FROM dbo.Loans WITH (UPDLOCK) WHERE Id = @LoanId AND IsDeleted = 0);

    IF @Status IS NULL
        THROW 50001, 'Loan not found', 1;

    IF @Status NOT IN ('Submitted', 'AwaitingValidation', 'CopiesConfirmed')
        THROW 50002, 'A loan can be cancelled only before the books are issued', 1;

    UPDATE dbo.Loans
    SET    Status    = 'Cancelled',
           UpdatedAt = SYSUTCDATETIME(),
           UpdatedBy = @ChangedBy
    WHERE  Id = @LoanId;

    INSERT INTO dbo.LoanStatusHistory (LoanId, FromStatus, ToStatus, ChangedBy, Note)
    VALUES (@LoanId, @Status, 'Cancelled', @ChangedBy, ISNULL(@Reason, 'Cancelled by librarian'));

    COMMIT TRANSACTION;
END
GO

/* =============================================================================
   6. usp_DecreaseBookCopies — точкове зменшення залишку з перевіркою.
      Використовується BLL у Лаб. 02 як ДРУГИЙ репозиторій транзакційного
      сценарію: штучна помилка тут відкочує зміну статусу видачі.
   ============================================================================= */
CREATE OR ALTER PROCEDURE dbo.usp_DecreaseBookCopies
    @BookId   BIGINT,
    @Quantity INT
AS
BEGIN
    SET NOCOUNT ON;

    IF @Quantity <= 0
        THROW 50004, 'Quantity must be positive', 1;

    DECLARE @Available INT =
        (SELECT CopiesAvailable FROM dbo.Books WITH (UPDLOCK) WHERE Id = @BookId);

    IF @Available IS NULL
        THROW 50001, 'Book not found', 1;

    IF @Available < @Quantity
        THROW 50003, 'Not enough copies available', 1;

    UPDATE dbo.Books
    SET    CopiesAvailable = CopiesAvailable - @Quantity
    WHERE  Id = @BookId;
END
GO

/* =============================================================================
   7. usp_GetReaderLoanSummary — читання/звіт: видачі читача з агрегатами.
      Приклад CRUD-процедури (Read) для DAL на Dapper.
   ============================================================================= */
CREATE OR ALTER PROCEDURE dbo.usp_GetReaderLoanSummary
    @ReaderId BIGINT,
    @Status   NVARCHAR(30) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Readers WHERE Id = @ReaderId AND IsDeleted = 0)
        THROW 50001, 'Reader not found', 1;

    SELECT  LoanId, Status, LoanDate, DueDate, ReturnedAt, FineAmount,
            ReaderId, ReaderName, CardNumber, PickupBranch,
            TitlesCount, UnitsTotal, DailyChargeTotal
    FROM    dbo.vw_LoanSummary
    WHERE   ReaderId = @ReaderId
      AND  (@Status IS NULL OR Status = @Status)
    ORDER BY LoanDate DESC;
END
GO

PRINT N'LibraryLendingDb: procedures.sql виконано успішно (7 процедур).';
GO
