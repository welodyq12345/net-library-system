/* =============================================================================
   ПРОЄКТ №2 — LibraryCatalogDb  (SQL Server + EF Core Code-First)
   Контекст: «Каталог» (Catalog) — довідник книг, авторів, жанрів і примірників.
   -----------------------------------------------------------------------------
   УВАГА. На етапі Лаб. 01 ця база ПРОЄКТУЄТЬСЯ. Цей файл — референсний DDL,
   що описує цільову схему; він слугує для перегляду ERD і для порівняння з
   тим, що згенерує перша міграція EF Core у Лаб. 03 (InitialCreate).
   Базу для Лаб. 03 створює команда:
       dotnet ef database update --project Library.Catalog.Dal
                                 --startup-project Library.Catalog.Api
   Виконувати цей скрипт вручну НЕ обов'язково — він документує схему.
   -----------------------------------------------------------------------------
   Типи зв'язків:
     1:1  Books   <-> BookDetails    (PK залежної таблиці = FK)
     1:N  Authors  -> Books          (FK на боці «багатьох»)
     1:N  Books    -> BookCopies
     M:N  Books   <-> Genres         (через BookGenres з власним полем AddedAt)
   Жодного FOREIGN KEY за межі цієї бази.
   ============================================================================= */

IF DB_ID('LibraryCatalogDb') IS NULL
    CREATE DATABASE LibraryCatalogDb;
GO

USE LibraryCatalogDb;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID('dbo.BookGenres', 'U')  IS NOT NULL DROP TABLE dbo.BookGenres;
IF OBJECT_ID('dbo.BookCopies', 'U')  IS NOT NULL DROP TABLE dbo.BookCopies;
IF OBJECT_ID('dbo.BookDetails', 'U') IS NOT NULL DROP TABLE dbo.BookDetails;
IF OBJECT_ID('dbo.Books', 'U')       IS NOT NULL DROP TABLE dbo.Books;
IF OBJECT_ID('dbo.Genres', 'U')      IS NOT NULL DROP TABLE dbo.Genres;
IF OBJECT_ID('dbo.Authors', 'U')     IS NOT NULL DROP TABLE dbo.Authors;
GO

/* =============================================================================
   1. Authors — автори (довідник). 1:N -> Books.
      PK: INT IDENTITY — довідник невеликий, вставка лише з цього сервісу.
   ============================================================================= */
CREATE TABLE dbo.Authors
(
    Id         INT           IDENTITY(1,1) CONSTRAINT PK_Authors PRIMARY KEY,
    FullName   NVARCHAR(200) NOT NULL,
    Country    NVARCHAR(100) NULL,
    BirthYear  INT           NULL CONSTRAINT CK_Authors_BirthYear CHECK (BirthYear IS NULL OR BirthYear BETWEEN 1000 AND 2100),
    CreatedAt  DATETIME2(3)  NOT NULL CONSTRAINT DF_Authors_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt  DATETIME2(3)  NULL,
    IsDeleted  BIT           NOT NULL CONSTRAINT DF_Authors_IsDeleted DEFAULT 0,
    RowVersion ROWVERSION
);
GO

/* індекс під запит: «пошук автора за прізвищем» */
CREATE INDEX IX_Authors_FullName ON dbo.Authors (FullName);
GO

/* =============================================================================
   2. Genres — жанри (довідник) для зв'язку M:N з книгами.
   ============================================================================= */
CREATE TABLE dbo.Genres
(
    Id          INT           IDENTITY(1,1) CONSTRAINT PK_Genres PRIMARY KEY,
    Name        NVARCHAR(100) NOT NULL CONSTRAINT UQ_Genres_Name UNIQUE,
    Slug        NVARCHAR(100) NOT NULL CONSTRAINT UQ_Genres_Slug UNIQUE,
    Description NVARCHAR(500) NULL,
    CreatedAt   DATETIME2(3)  NOT NULL CONSTRAINT DF_Genres_CreatedAt DEFAULT SYSUTCDATETIME(),
    IsDeleted   BIT           NOT NULL CONSTRAINT DF_Genres_IsDeleted DEFAULT 0
);
GO

/* =============================================================================
   3. Books — коренева сутність каталогу (аналог CatalogItem в eShop).
      Бізнес-правила (ціна > 0, достатній залишок) реалізовано МЕТОДАМИ
      сутності в Лаб. 03 (RemoveStock / AddStock), а не відкритими setter-ами.
      Books.Id — ВЛАСНИК ідентифікатора книги в системі: саме цей Id
      копіюється в LibraryLendingDb.Books та в reviews.bookId (MongoDB).
   ============================================================================= */
CREATE TABLE dbo.Books
(
    Id              BIGINT        IDENTITY(1,1) CONSTRAINT PK_Books PRIMARY KEY,
    Title           NVARCHAR(300) NOT NULL,
    Isbn            NVARCHAR(20)  NOT NULL CONSTRAINT UQ_Books_Isbn UNIQUE,  -- унікальний бізнес-ключ
    DailyFee        DECIMAL(18,2) NOT NULL CONSTRAINT CK_Books_DailyFee CHECK (DailyFee > 0),
    AvailableCopies INT           NOT NULL CONSTRAINT DF_Books_AvailableCopies DEFAULT 0
                                  CONSTRAINT CK_Books_AvailableCopies CHECK (AvailableCopies >= 0),
    PublishedYear   INT           NULL CONSTRAINT CK_Books_PublishedYear CHECK (PublishedYear IS NULL OR PublishedYear BETWEEN 1400 AND 2100),
    AuthorId        INT           NOT NULL
                    CONSTRAINT FK_Books_Authors REFERENCES dbo.Authors (Id),  -- 1:N, БЕЗ каскаду:
                                                                              -- автора з книгами не видаляємо
    CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_Books_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2(3)  NULL,
    IsDeleted       BIT           NOT NULL CONSTRAINT DF_Books_IsDeleted DEFAULT 0,
    RowVersion      ROWVERSION                                                -- оптимістична конкурентність
);
GO

/* індекс під запит: «пошук книг за назвою з фільтром за тарифом» (список API з пагінацією) */
CREATE INDEX IX_Books_Title ON dbo.Books (Title) INCLUDE (DailyFee, AvailableCopies, AuthorId);
GO
/* індекс під запит: «усі книги автора» */
CREATE INDEX IX_Books_AuthorId ON dbo.Books (AuthorId);
GO
/* фільтрований індекс під запит: «наявні в каталозі книги» */
CREATE INDEX IX_Books_Active ON dbo.Books (Title) WHERE IsDeleted = 0;
GO

/* =============================================================================
   4. BookDetails — 1:1 до Books. PK є водночас FK.
      Каскадне видалення доречне: деталі не існують без книги.
   ============================================================================= */
CREATE TABLE dbo.BookDetails
(
    BookId      BIGINT         NOT NULL
                CONSTRAINT PK_BookDetails PRIMARY KEY
                CONSTRAINT FK_BookDetails_Books REFERENCES dbo.Books (Id) ON DELETE CASCADE,
    Description NVARCHAR(2000) NOT NULL,
    Publisher   NVARCHAR(200)  NULL,
    PageCount   INT            NULL CONSTRAINT CK_BookDetails_PageCount CHECK (PageCount IS NULL OR PageCount > 0),
    Language    NVARCHAR(50)   NOT NULL CONSTRAINT DF_BookDetails_Language DEFAULT N'uk',
    CoverUrl    NVARCHAR(500)  NULL
);
GO

/* =============================================================================
   5. BookCopies — 1:N до Books: фізичні примірники на полицях.
      Каскадне видалення доречне: примірник не існує без книги.
   ============================================================================= */
CREATE TABLE dbo.BookCopies
(
    Id              BIGINT       IDENTITY(1,1) CONSTRAINT PK_BookCopies PRIMARY KEY,
    BookId          BIGINT       NOT NULL
                    CONSTRAINT FK_BookCopies_Books REFERENCES dbo.Books (Id) ON DELETE CASCADE,
    InventoryNumber NVARCHAR(40) NOT NULL CONSTRAINT UQ_BookCopies_InventoryNumber UNIQUE,
    Shelf           NVARCHAR(40) NULL,
    [Condition]     NVARCHAR(20) NOT NULL CONSTRAINT DF_BookCopies_Condition DEFAULT N'Good'
                    CONSTRAINT CK_BookCopies_Condition CHECK ([Condition] IN (N'New', N'Good', N'Worn', N'Damaged', N'Lost')),
    AcquiredAt      DATE         NULL
);
GO

/* індекс під запит: «усі примірники книги з їх станом» */
CREATE INDEX IX_BookCopies_BookId ON dbo.BookCopies (BookId) INCLUDE ([Condition], Shelf);
GO

/* =============================================================================
   6. BookGenres — M:N між Books і Genres ЧЕРЕЗ ПРОМІЖНУ СУТНІСТЬ
      З ВЛАСНИМ ПОЛЕМ AddedAt. Автоматичний M:N в EF Core такого поля
      зберегти не дозволив би — саме тому сутність описана явно.
   ============================================================================= */
CREATE TABLE dbo.BookGenres
(
    BookId   BIGINT       NOT NULL
             CONSTRAINT FK_BookGenres_Books  REFERENCES dbo.Books  (Id) ON DELETE CASCADE,
    GenreId  INT          NOT NULL
             CONSTRAINT FK_BookGenres_Genres REFERENCES dbo.Genres (Id),
    AddedAt  DATETIME2(3) NOT NULL CONSTRAINT DF_BookGenres_AddedAt DEFAULT SYSUTCDATETIME(),  -- власне поле M:N
    AddedBy  NVARCHAR(100) NULL,
    IsPrimary BIT          NOT NULL CONSTRAINT DF_BookGenres_IsPrimary DEFAULT 0,              -- головний жанр книги

    CONSTRAINT PK_BookGenres PRIMARY KEY (BookId, GenreId)      -- складений ключ
);
GO

/* індекс під запит: «нові книги жанру за період» (LINQ-запит через проміжну сутність, Лаб. 03) */
CREATE INDEX IX_BookGenres_GenreId_AddedAt ON dbo.BookGenres (GenreId, AddedAt DESC) INCLUDE (BookId);
GO

PRINT N'LibraryCatalogDb: schema.sql виконано успішно.';
GO
