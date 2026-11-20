/* =============================================================================
   ПРОЄКТ №1 — LibraryLendingDb  (SQL Server + ADO.NET & Dapper)
   Контекст: «Видача» (Lending) — транзакційний процес видачі книг читачам.
   -----------------------------------------------------------------------------
   Файл: schema.sql — DDL (таблиці, ключі, обмеження, індекси).
   Порядок запуску: schema.sql -> procedures.sql -> seed.sql -> verify.sql
   -----------------------------------------------------------------------------
   Типи зв'язків, продемонстровані в цій базі:
     1:1  Loans          <-> LoanDetails        (PK залежної таблиці = FK)
     1:N  Readers         -> Loans              (FK на боці «багатьох»)
     M:N  Loans          <-> Books              (через LoanItems)
   Посилання на інші контексти — БЕЗ FOREIGN KEY:
     Books.Id            -> Book.Id   у LibraryCatalogDb (Проєкт №2)
   ============================================================================= */

IF DB_ID('LibraryLendingDb') IS NULL
    CREATE DATABASE LibraryLendingDb;
GO

USE LibraryLendingDb;
GO

/* Налаштування сеансу, ОБОВ'ЯЗКОВІ для фільтрованих індексів.
   sqlcmd за замовчуванням має QUOTED_IDENTIFIER OFF, тому задаємо явно
   (або запускайте sqlcmd з ключем -I). */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------- ідемпотентність: прибираємо об'єкти у зворотному порядку ---------- */
IF OBJECT_ID('dbo.FinePayments', 'U')      IS NOT NULL DROP TABLE dbo.FinePayments;
IF OBJECT_ID('dbo.LoanStatusHistory', 'U') IS NOT NULL DROP TABLE dbo.LoanStatusHistory;
IF OBJECT_ID('dbo.LoanItems', 'U')         IS NOT NULL DROP TABLE dbo.LoanItems;
IF OBJECT_ID('dbo.LoanDetails', 'U')       IS NOT NULL DROP TABLE dbo.LoanDetails;
IF OBJECT_ID('dbo.Loans', 'U')             IS NOT NULL DROP TABLE dbo.Loans;
IF OBJECT_ID('dbo.Books', 'U')             IS NOT NULL DROP TABLE dbo.Books;
IF OBJECT_ID('dbo.Readers', 'U')           IS NOT NULL DROP TABLE dbo.Readers;
GO

/* =============================================================================
   1. Readers — читачі бібліотеки. Власник даних: цей контекст.
      Аналог Buyer / Customer у референсному застосунку eShop.
      PK: BIGINT IDENTITY — вставка йде лише з цього сервісу, послідовні
      значення дають компактні індекси (GUID тут не потрібен).
   ============================================================================= */
CREATE TABLE dbo.Readers
(
    Id          BIGINT         IDENTITY(1,1) CONSTRAINT PK_Readers PRIMARY KEY,
    CardNumber  NVARCHAR(20)   NOT NULL CONSTRAINT UQ_Readers_CardNumber UNIQUE,
    Email       NVARCHAR(256)  NOT NULL CONSTRAINT UQ_Readers_Email      UNIQUE,
    FullName    NVARCHAR(200)  NOT NULL,
    Phone       NVARCHAR(30)   NULL,
    IsBlocked   BIT            NOT NULL CONSTRAINT DF_Readers_IsBlocked DEFAULT 0,

    -- аудитні колонки
    CreatedAt   DATETIME2(3)   NOT NULL CONSTRAINT DF_Readers_CreatedAt DEFAULT SYSUTCDATETIME(),
    CreatedBy   NVARCHAR(100)  NULL,
    UpdatedAt   DATETIME2(3)   NULL,
    UpdatedBy   NVARCHAR(100)  NULL,
    IsDeleted   BIT            NOT NULL CONSTRAINT DF_Readers_IsDeleted DEFAULT 0,  -- soft delete
    RowVersion  ROWVERSION,                                                          -- оптимістична конкурентність

    CONSTRAINT CK_Readers_Email CHECK (Email LIKE '%_@_%._%')
);
GO

/* індекс під запит: «знайти читача за номером картки серед активних»
   (фільтрований — обслуговує лише «живі» записи, менший за розміром)        */
CREATE INDEX IX_Readers_Active_CardNumber
    ON dbo.Readers (CardNumber) INCLUDE (FullName, Email)
    WHERE IsDeleted = 0;
GO

/* =============================================================================
   2. Books — ЛОКАЛЬНА КОПІЯ ДОВІДНИКА з БД Каталогу (Проєкт №2).
      Id — той самий ідентифікатор, що й Book.Id у LibraryCatalogDb,
      але FOREIGN KEY між базами НЕ створюється (database-per-service).
      Оновлюється подіями з Каталогу (див. таблицю політики дублювання).
   ============================================================================= */
CREATE TABLE dbo.Books
(
    Id               BIGINT        NOT NULL CONSTRAINT PK_Books PRIMARY KEY,  -- без IDENTITY: Id приходить з Каталогу
    Title            NVARCHAR(300) NOT NULL,
    Author           NVARCHAR(200) NOT NULL,
    Isbn             NVARCHAR(20)  NULL,
    DailyFee         DECIMAL(18,2) NOT NULL CONSTRAINT CK_Books_DailyFee CHECK (DailyFee > 0),
    CopiesAvailable  INT           NOT NULL CONSTRAINT DF_Books_CopiesAvailable DEFAULT 0
                                   CONSTRAINT CK_Books_CopiesAvailable CHECK (CopiesAvailable >= 0),
    SyncedAt         DATETIME2(3)  NOT NULL CONSTRAINT DF_Books_SyncedAt DEFAULT SYSUTCDATETIME(),
    RowVersion       ROWVERSION
);
GO

/* індекс під запит: «пошук книги за назвою у формі видачі» */
CREATE INDEX IX_Books_Title ON dbo.Books (Title) INCLUDE (Author, DailyFee, CopiesAvailable);
GO

/* =============================================================================
   3. Loans — видача (аналог Order в eShop). Коренева сутність агрегату.
      1:N  Readers -> Loans : FK стоїть на боці «багатьох».
      Статуси та допустимі переходи (за зразком OrderStatus з eShop):
        Submitted -> AwaitingValidation -> CopiesConfirmed -> Issued -> Returned
        Cancelled — лише ДО видачі книг на руки (Issued).
   ============================================================================= */
CREATE TABLE dbo.Loans
(
    Id          BIGINT        IDENTITY(1,1) CONSTRAINT PK_Loans PRIMARY KEY,
    ReaderId    BIGINT        NOT NULL
                CONSTRAINT FK_Loans_Readers REFERENCES dbo.Readers (Id),   -- БЕЗ каскаду:
                                                                           -- читача з історією не видаляємо
    Status      NVARCHAR(30)  NOT NULL CONSTRAINT DF_Loans_Status DEFAULT 'Submitted'
                CONSTRAINT CK_Loans_Status CHECK (Status IN
                    ('Submitted', 'AwaitingValidation', 'CopiesConfirmed',
                     'Issued', 'Returned', 'Cancelled')),
    LoanDate    DATETIME2(3)  NOT NULL CONSTRAINT DF_Loans_LoanDate DEFAULT SYSUTCDATETIME(),
    DueDate     DATE          NOT NULL,                        -- коли треба повернути
    IssuedAt    DATETIME2(3)  NULL,
    ReturnedAt  DATETIME2(3)  NULL,
    FineAmount  DECIMAL(18,2) NOT NULL CONSTRAINT DF_Loans_FineAmount DEFAULT 0
                CONSTRAINT CK_Loans_FineAmount CHECK (FineAmount >= 0),

    CreatedAt   DATETIME2(3)  NOT NULL CONSTRAINT DF_Loans_CreatedAt DEFAULT SYSUTCDATETIME(),
    CreatedBy   NVARCHAR(100) NULL,
    UpdatedAt   DATETIME2(3)  NULL,
    UpdatedBy   NVARCHAR(100) NULL,
    IsDeleted   BIT           NOT NULL CONSTRAINT DF_Loans_IsDeleted DEFAULT 0,
    RowVersion  ROWVERSION,

    CONSTRAINT CK_Loans_DueAfterLoan CHECK (DueDate >= CAST(LoanDate AS DATE))
);
GO

/* індекс під запит: «видачі читача з певним статусом» (основний список в API) */
CREATE INDEX IX_Loans_ReaderId_Status ON dbo.Loans (ReaderId, Status) INCLUDE (DueDate, FineAmount);
GO
/* індекс під запит: «прострочені видачі» — фільтрований, лише видані на руки */
CREATE INDEX IX_Loans_Overdue ON dbo.Loans (DueDate) INCLUDE (ReaderId)
    WHERE Status = 'Issued' AND IsDeleted = 0;
GO

/* =============================================================================
   4. LoanDetails — 1:1 до Loans. Первинний ключ є водночас зовнішнім ключем,
      тому на одну видачу може існувати не більше одного рядка деталей.
      Каскадне видалення тут доречне: деталі не існують без видачі.
   ============================================================================= */
CREATE TABLE dbo.LoanDetails
(
    LoanId          BIGINT         NOT NULL
                    CONSTRAINT PK_LoanDetails PRIMARY KEY
                    CONSTRAINT FK_LoanDetails_Loans REFERENCES dbo.Loans (Id) ON DELETE CASCADE,
    PickupBranch    NVARCHAR(100)  NOT NULL,
    LibrarianName   NVARCHAR(200)  NULL,
    Comment         NVARCHAR(1000) NULL
);
GO

/* =============================================================================
   5. LoanItems — M:N між Loans і Books через проміжну таблицю з власними полями.
      BookTitle / BookAuthor / DailyFee — ЗНІМОК на момент оформлення видачі
      (так само, як OrderItem в eShop зберігає ProductName і UnitPrice).
      Знімок свідомо НЕ оновлюється при зміні каталогу.
   ============================================================================= */
CREATE TABLE dbo.LoanItems
(
    LoanId      BIGINT        NOT NULL
                CONSTRAINT FK_LoanItems_Loans REFERENCES dbo.Loans (Id) ON DELETE CASCADE,
    BookId      BIGINT        NOT NULL
                CONSTRAINT FK_LoanItems_Books REFERENCES dbo.Books (Id),
    BookTitle   NVARCHAR(300) NOT NULL,                        -- знімок
    BookAuthor  NVARCHAR(200) NOT NULL,                        -- знімок
    DailyFee    DECIMAL(18,2) NOT NULL CONSTRAINT CK_LoanItems_DailyFee CHECK (DailyFee > 0),  -- знімок
    Units       INT           NOT NULL CONSTRAINT CK_LoanItems_Units CHECK (Units > 0),
    Discount    DECIMAL(18,2) NOT NULL CONSTRAINT DF_LoanItems_Discount DEFAULT 0
                CONSTRAINT CK_LoanItems_Discount CHECK (Discount >= 0),

    CONSTRAINT PK_LoanItems PRIMARY KEY (LoanId, BookId)       -- складений ключ M:N
);
GO

/* індекс під запит: «у яких видачах фігурує ця книга» (зворотний бік складеного PK) */
CREATE INDEX IX_LoanItems_BookId ON dbo.LoanItems (BookId) INCLUDE (Units);
GO

/* =============================================================================
   6. LoanStatusHistory — журнал переходів статусів (1:N від Loans).
      Пишеться збережуваними процедурами в тій самій транзакції, що й зміна статусу.
   ============================================================================= */
CREATE TABLE dbo.LoanStatusHistory
(
    Id          BIGINT        IDENTITY(1,1) CONSTRAINT PK_LoanStatusHistory PRIMARY KEY,
    LoanId      BIGINT        NOT NULL
                CONSTRAINT FK_LoanStatusHistory_Loans REFERENCES dbo.Loans (Id) ON DELETE CASCADE,
    FromStatus  NVARCHAR(30)  NULL,
    ToStatus    NVARCHAR(30)  NOT NULL,
    ChangedAt   DATETIME2(3)  NOT NULL CONSTRAINT DF_LoanStatusHistory_ChangedAt DEFAULT SYSUTCDATETIME(),
    ChangedBy   NVARCHAR(100) NULL,
    Note        NVARCHAR(400) NULL
);
GO

/* індекс під запит: «історія однієї видачі в хронологічному порядку» */
CREATE INDEX IX_LoanStatusHistory_LoanId_ChangedAt ON dbo.LoanStatusHistory (LoanId, ChangedAt);
GO

/* =============================================================================
   7. FinePayments — оплати пені (1:N від Loans). Транзакційні дані контексту.
   ============================================================================= */
CREATE TABLE dbo.FinePayments
(
    Id              BIGINT        IDENTITY(1,1) CONSTRAINT PK_FinePayments PRIMARY KEY,
    LoanId          BIGINT        NOT NULL
                    CONSTRAINT FK_FinePayments_Loans REFERENCES dbo.Loans (Id) ON DELETE CASCADE,
    Amount          DECIMAL(18,2) NOT NULL CONSTRAINT CK_FinePayments_Amount CHECK (Amount > 0),
    Method          NVARCHAR(30)  NOT NULL CONSTRAINT DF_FinePayments_Method DEFAULT 'Cash'
                    CONSTRAINT CK_FinePayments_Method CHECK (Method IN ('Cash', 'Card', 'Online')),
    PaidAt          DATETIME2(3)  NOT NULL CONSTRAINT DF_FinePayments_PaidAt DEFAULT SYSUTCDATETIME(),
    IdempotencyKey  NVARCHAR(64)  NULL CONSTRAINT UQ_FinePayments_IdempotencyKey UNIQUE  -- захист від повторів
);
GO

/* індекс під запит: «усі оплати по видачі» */
CREATE INDEX IX_FinePayments_LoanId ON dbo.FinePayments (LoanId) INCLUDE (Amount, PaidAt);
GO

/* =============================================================================
   Денормалізоване представлення для читань (звіт без ручних JOIN-ів у коді).
   ============================================================================= */
IF OBJECT_ID('dbo.vw_LoanSummary', 'V') IS NOT NULL DROP VIEW dbo.vw_LoanSummary;
GO
CREATE VIEW dbo.vw_LoanSummary
AS
SELECT  l.Id            AS LoanId,
        l.Status,
        l.LoanDate,
        l.DueDate,
        l.ReturnedAt,
        l.FineAmount,
        r.Id            AS ReaderId,
        r.FullName      AS ReaderName,
        r.CardNumber,
        d.PickupBranch,
        COUNT(i.BookId) AS TitlesCount,
        ISNULL(SUM(i.Units), 0) AS UnitsTotal,
        ISNULL(SUM(i.DailyFee * i.Units - i.Discount), 0) AS DailyChargeTotal
FROM        dbo.Loans        AS l
INNER JOIN  dbo.Readers      AS r ON r.Id = l.ReaderId
LEFT  JOIN  dbo.LoanDetails  AS d ON d.LoanId = l.Id
LEFT  JOIN  dbo.LoanItems    AS i ON i.LoanId = l.Id
WHERE   l.IsDeleted = 0
GROUP BY l.Id, l.Status, l.LoanDate, l.DueDate, l.ReturnedAt, l.FineAmount,
         r.Id, r.FullName, r.CardNumber, d.PickupBranch;
GO

PRINT N'LibraryLendingDb: schema.sql виконано успішно.';
GO
