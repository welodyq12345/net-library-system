IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE TABLE [Authors] (
        [Id] int NOT NULL IDENTITY,
        [FullName] nvarchar(200) NOT NULL,
        [Country] nvarchar(100) NULL,
        [BirthYear] int NULL,
        [CreatedAt] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] datetime2 NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Authors] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Authors_BirthYear] CHECK ([BirthYear] IS NULL OR ([BirthYear] >= 1000 AND [BirthYear] <= 2100))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE TABLE [Genres] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        [Slug] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NULL,
        [CreatedAt] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        CONSTRAINT [PK_Genres] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE TABLE [Books] (
        [Id] bigint NOT NULL IDENTITY,
        [Title] nvarchar(300) NOT NULL,
        [Isbn] nvarchar(20) NOT NULL,
        [DailyFee] decimal(18,2) NOT NULL,
        [AvailableCopies] int NOT NULL DEFAULT 0,
        [PublishedYear] int NULL,
        [AuthorId] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] datetime2 NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Books] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Books_AvailableCopies] CHECK ([AvailableCopies] >= 0),
        CONSTRAINT [CK_Books_DailyFee] CHECK ([DailyFee] > 0),
        CONSTRAINT [CK_Books_PublishedYear] CHECK ([PublishedYear] IS NULL OR ([PublishedYear] >= 1400 AND [PublishedYear] <= 2100)),
        CONSTRAINT [FK_Books_Authors_AuthorId] FOREIGN KEY ([AuthorId]) REFERENCES [Authors] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE TABLE [BookCopies] (
        [Id] bigint NOT NULL IDENTITY,
        [BookId] bigint NOT NULL,
        [InventoryNumber] nvarchar(40) NOT NULL,
        [Shelf] nvarchar(40) NULL,
        [Condition] nvarchar(20) NOT NULL DEFAULT N'Good',
        [AcquiredAt] date NULL,
        CONSTRAINT [PK_BookCopies] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_BookCopies_Condition] CHECK ([Condition] IN ('New', 'Good', 'Worn', 'Damaged', 'Lost')),
        CONSTRAINT [FK_BookCopies_Books_BookId] FOREIGN KEY ([BookId]) REFERENCES [Books] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE TABLE [BookDetails] (
        [BookId] bigint NOT NULL,
        [Description] nvarchar(2000) NOT NULL,
        [Publisher] nvarchar(200) NULL,
        [PageCount] int NULL,
        [Language] nvarchar(50) NOT NULL DEFAULT N'uk',
        [CoverUrl] nvarchar(500) NULL,
        CONSTRAINT [PK_BookDetails] PRIMARY KEY ([BookId]),
        CONSTRAINT [CK_BookDetails_PageCount] CHECK ([PageCount] IS NULL OR [PageCount] > 0),
        CONSTRAINT [FK_BookDetails_Books_BookId] FOREIGN KEY ([BookId]) REFERENCES [Books] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE TABLE [BookGenres] (
        [BookId] bigint NOT NULL,
        [GenreId] int NOT NULL,
        [AddedAt] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        [AddedBy] nvarchar(100) NULL,
        [IsPrimary] bit NOT NULL DEFAULT CAST(0 AS bit),
        CONSTRAINT [PK_BookGenres] PRIMARY KEY ([BookId], [GenreId]),
        CONSTRAINT [FK_BookGenres_Books_BookId] FOREIGN KEY ([BookId]) REFERENCES [Books] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_BookGenres_Genres_GenreId] FOREIGN KEY ([GenreId]) REFERENCES [Genres] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'BirthYear', N'Country', N'CreatedAt', N'FullName', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[Authors]'))
        SET IDENTITY_INSERT [Authors] ON;
    EXEC(N'INSERT INTO [Authors] ([Id], [BirthYear], [Country], [CreatedAt], [FullName], [UpdatedAt])
    VALUES (1, 1814, N''Україна'', ''2026-01-01T00:00:00.0000000Z'', N''Тарас Шевченко'', NULL),
    (2, 1906, N''Україна'', ''2026-01-01T00:00:00.0000000Z'', N''Іван Багряний'', NULL),
    (3, 1901, N''Україна'', ''2026-01-01T00:00:00.0000000Z'', N''Валер''''ян Підмогильний'', NULL),
    (4, 1974, N''Україна'', ''2026-01-01T00:00:00.0000000Z'', N''Сергій Жадан'', NULL),
    (5, 1951, N''Україна'', ''2026-01-01T00:00:00.0000000Z'', N''Василь Шкляр'', NULL),
    (6, 1930, N''Україна'', ''2026-01-01T00:00:00.0000000Z'', N''Ліна Костенко'', NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'BirthYear', N'Country', N'CreatedAt', N'FullName', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[Authors]'))
        SET IDENTITY_INSERT [Authors] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CreatedAt', N'Description', N'Name', N'Slug') AND [object_id] = OBJECT_ID(N'[Genres]'))
        SET IDENTITY_INSERT [Genres] ON;
    EXEC(N'INSERT INTO [Genres] ([Id], [CreatedAt], [Description], [Name], [Slug])
    VALUES (1, ''2026-01-01T00:00:00.0000000Z'', N''Віршовані твори'', N''Поезія'', N''poetry''),
    (2, ''2026-01-01T00:00:00.0000000Z'', N''Проза на історичному матеріалі'', N''Історичний роман'', N''historical''),
    (3, ''2026-01-01T00:00:00.0000000Z'', N''Українська література XX–XXI ст.'', N''Сучасна проза'', N''modern''),
    (4, ''2026-01-01T00:00:00.0000000Z'', N''Канонічні твори'', N''Класика'', N''classic''),
    (5, ''2026-01-01T00:00:00.0000000Z'', N''Нехудожня література'', N''Публіцистика'', N''nonfiction'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CreatedAt', N'Description', N'Name', N'Slug') AND [object_id] = OBJECT_ID(N'[Genres]'))
        SET IDENTITY_INSERT [Genres] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Authors_FullName] ON [Authors] ([FullName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_BookCopies_BookId] ON [BookCopies] ([BookId]) INCLUDE ([Condition], [Shelf]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_BookCopies_InventoryNumber] ON [BookCopies] ([InventoryNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_BookGenres_GenreId_AddedAt] ON [BookGenres] ([GenreId], [AddedAt] DESC) INCLUDE ([BookId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Books_Active] ON [Books] ([Title]) INCLUDE ([DailyFee], [AvailableCopies], [AuthorId]) WHERE [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Books_AuthorId] ON [Books] ([AuthorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_Books_Isbn] ON [Books] ([Isbn]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_Genres_Name] ON [Genres] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_Genres_Slug] ON [Genres] ([Slug]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120002544_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261120002544_InitialCreate', N'9.0.11');
END;

COMMIT;
GO

