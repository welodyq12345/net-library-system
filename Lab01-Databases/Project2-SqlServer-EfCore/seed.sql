/* =============================================================================
   ПРОЄКТ №2 — LibraryCatalogDb : початкові довідникові дані
   -----------------------------------------------------------------------------
   Ці ж дані в Лаб. 03 подаються двома способами:
     - статичний сідінг  — HasData у конфігураціях (Authors, Genres: фіксовані Id);
     - програмний сідінг — DbInitializer.SeedAsync після MigrateAsync (Books та
                           залежні записи).
   ІДЕМПОТЕНТНІСТЬ: усі вставки захищені перевіркою наявності.
   Порядок дотримує цілісність FK: спочатку довідники, потім залежні записи.
   ============================================================================= */

USE LibraryCatalogDb;
GO
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------------------------------------------------------------------------
   1. Authors — довідник із фіксованими Id (статичний сідінг HasData)
   --------------------------------------------------------------------------- */
SET IDENTITY_INSERT dbo.Authors ON;

INSERT INTO dbo.Authors (Id, FullName, Country, BirthYear)
SELECT v.Id, v.FullName, v.Country, v.BirthYear
FROM (VALUES
        (1, N'Тарас Шевченко',          N'Україна', 1814),
        (2, N'Іван Багряний',           N'Україна', 1906),
        (3, N'Валер''ян Підмогильний',  N'Україна', 1901),
        (4, N'Сергій Жадан',            N'Україна', 1974),
        (5, N'Василь Шкляр',            N'Україна', 1951),
        (6, N'Ліна Костенко',           N'Україна', 1930)
     ) AS v (Id, FullName, Country, BirthYear)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Authors a WHERE a.Id = v.Id);

SET IDENTITY_INSERT dbo.Authors OFF;
GO

/* ---------------------------------------------------------------------------
   2. Genres — довідник із фіксованими Id (статичний сідінг HasData)
   --------------------------------------------------------------------------- */
SET IDENTITY_INSERT dbo.Genres ON;

INSERT INTO dbo.Genres (Id, Name, Slug, Description)
SELECT v.Id, v.Name, v.Slug, v.Description
FROM (VALUES
        (1, N'Поезія',            N'poetry',      N'Віршовані твори'),
        (2, N'Історичний роман',  N'historical',  N'Проза на історичному матеріалі'),
        (3, N'Сучасна проза',     N'modern',      N'Українська література XX–XXI ст.'),
        (4, N'Класика',           N'classic',     N'Канонічні твори'),
        (5, N'Публіцистика',      N'nonfiction',  N'Нехудожня література')
     ) AS v (Id, Name, Slug, Description)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Genres g WHERE g.Id = v.Id);

SET IDENTITY_INSERT dbo.Genres OFF;
GO

/* ---------------------------------------------------------------------------
   3. Books — залежні записи (програмний сідінг у Лаб. 03).
      Id фіксуємо явно, бо саме ці Id дублюються в LibraryLendingDb.Books
      та в reviews.bookId (MongoDB) — див. таблицю політики дублювання.
   --------------------------------------------------------------------------- */
SET IDENTITY_INSERT dbo.Books ON;

INSERT INTO dbo.Books (Id, Title, Isbn, DailyFee, AvailableCopies, PublishedYear, AuthorId)
SELECT v.Id, v.Title, v.Isbn, v.DailyFee, v.AvailableCopies, v.PublishedYear, v.AuthorId
FROM (VALUES
        (1, N'Кобзар',                              N'978-966-03-5555-1', CAST(3.50 AS DECIMAL(18,2)), 5, 1840, 1),
        (2, N'Тигролови',                           N'978-966-03-5555-2', CAST(4.00 AS DECIMAL(18,2)), 3, 1944, 2),
        (3, N'Місто',                               N'978-966-03-5555-3', CAST(4.25 AS DECIMAL(18,2)), 4, 1928, 3),
        (4, N'Інтернат',                            N'978-966-03-5555-4', CAST(5.00 AS DECIMAL(18,2)), 2, 2017, 4),
        (5, N'Чорний Ворон',                        N'978-966-03-5555-5', CAST(4.75 AS DECIMAL(18,2)), 6, 2009, 5),
        (6, N'Записки українського самашедшого',    N'978-966-03-5555-6', CAST(5.50 AS DECIMAL(18,2)), 1, 2010, 6)
     ) AS v (Id, Title, Isbn, DailyFee, AvailableCopies, PublishedYear, AuthorId)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Books b WHERE b.Id = v.Id);

SET IDENTITY_INSERT dbo.Books OFF;
GO

/* ---------------------------------------------------------------------------
   4. BookDetails — 1:1
   --------------------------------------------------------------------------- */
INSERT INTO dbo.BookDetails (BookId, Description, Publisher, PageCount, Language)
SELECT v.BookId, v.Description, v.Publisher, v.PageCount, N'uk'
FROM (VALUES
        (1, N'Збірка поетичних творів Тараса Шевченка, основа української літератури.', N'Дніпро',   368),
        (2, N'Пригодницький роман про втечу з ешелону і виживання в тайзі.',             N'Смолоскип', 304),
        (3, N'Урбаністичний роман про перетворення селянина на письменника.',            N'Основи',   288),
        (4, N'Роман про три дні з життя вчителя на тлі війни на сході України.',          N'Meridian Czernowitz', 336),
        (5, N'Роман про отамана Чорного Ворона та повстанський рух 1920-х.',             N'Ярославів Вал', 432),
        (6, N'Роман-щоденник про події початку 2000-х в Україні.',                       N'Либідь',   416)
     ) AS v (BookId, Description, Publisher, PageCount)
WHERE NOT EXISTS (SELECT 1 FROM dbo.BookDetails d WHERE d.BookId = v.BookId);
GO

/* ---------------------------------------------------------------------------
   5. BookCopies — 1:N (фізичні примірники)
   --------------------------------------------------------------------------- */
INSERT INTO dbo.BookCopies (BookId, InventoryNumber, Shelf, [Condition], AcquiredAt)
SELECT v.BookId, v.InventoryNumber, v.Shelf, v.Cond, v.AcquiredAt
FROM (VALUES
        (1, N'INV-0001', N'A-01', N'Good',    CAST('2023-02-10' AS DATE)),
        (1, N'INV-0002', N'A-01', N'New',     CAST('2025-05-04' AS DATE)),
        (2, N'INV-0003', N'A-02', N'Good',    CAST('2022-11-20' AS DATE)),
        (3, N'INV-0004', N'B-01', N'Worn',    CAST('2019-09-01' AS DATE)),
        (4, N'INV-0005', N'B-02', N'New',     CAST('2026-01-15' AS DATE)),
        (5, N'INV-0006', N'C-01', N'Good',    CAST('2021-06-30' AS DATE)),
        (6, N'INV-0007', N'C-02', N'Good',    CAST('2024-03-12' AS DATE))
     ) AS v (BookId, InventoryNumber, Shelf, Cond, AcquiredAt)
WHERE NOT EXISTS (SELECT 1 FROM dbo.BookCopies c WHERE c.InventoryNumber = v.InventoryNumber);
GO

/* ---------------------------------------------------------------------------
   6. BookGenres — M:N із власним полем AddedAt
   --------------------------------------------------------------------------- */
INSERT INTO dbo.BookGenres (BookId, GenreId, AddedAt, AddedBy, IsPrimary)
SELECT v.BookId, v.GenreId, v.AddedAt, N'seed', v.IsPrimary
FROM (VALUES
        (1, 1, CAST('2026-09-01T10:00:00' AS DATETIME2(3)), 1),
        (1, 4, CAST('2026-09-01T10:00:00' AS DATETIME2(3)), 0),
        (2, 2, CAST('2026-09-05T10:00:00' AS DATETIME2(3)), 1),
        (2, 4, CAST('2026-09-05T10:00:00' AS DATETIME2(3)), 0),
        (3, 3, CAST('2026-10-01T10:00:00' AS DATETIME2(3)), 1),
        (3, 4, CAST('2026-10-01T10:00:00' AS DATETIME2(3)), 0),
        (4, 3, CAST('2026-11-01T10:00:00' AS DATETIME2(3)), 1),
        (5, 2, CAST('2026-11-10T10:00:00' AS DATETIME2(3)), 1),
        (5, 3, CAST('2026-11-10T10:00:00' AS DATETIME2(3)), 0),
        (6, 5, CAST('2026-11-15T10:00:00' AS DATETIME2(3)), 1)
     ) AS v (BookId, GenreId, AddedAt, IsPrimary)
WHERE NOT EXISTS (SELECT 1 FROM dbo.BookGenres bg
                  WHERE bg.BookId = v.BookId AND bg.GenreId = v.GenreId);
GO

PRINT N'LibraryCatalogDb: seed.sql виконано успішно (повторний запуск дублікатів не створює).';
GO

SELECT 'Authors' AS [Table], COUNT(*) AS [Rows] FROM dbo.Authors
UNION ALL SELECT 'Genres',      COUNT(*) FROM dbo.Genres
UNION ALL SELECT 'Books',       COUNT(*) FROM dbo.Books
UNION ALL SELECT 'BookDetails', COUNT(*) FROM dbo.BookDetails
UNION ALL SELECT 'BookCopies',  COUNT(*) FROM dbo.BookCopies
UNION ALL SELECT 'BookGenres',  COUNT(*) FROM dbo.BookGenres;
GO
