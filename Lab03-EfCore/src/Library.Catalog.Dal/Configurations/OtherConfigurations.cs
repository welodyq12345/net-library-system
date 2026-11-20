using Library.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Library.Catalog.Dal.Configurations;

/// <summary>
/// Фіксована дата для <c>HasData</c>. Значення в модельному сідінгу мають бути
/// СТАТИЧНИМИ: EF Core порівнює їх між міграціями, і <c>DateTime.UtcNow</c>
/// давав би нову міграцію при кожній генерації.
/// </summary>
internal static class SeedConstants
{
    public static readonly DateTime SeedDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}

/// <summary>Конфігурація <see cref="Author"/> + СТАТИЧНИЙ сідінг довідника.</summary>
public sealed class AuthorConfiguration : IEntityTypeConfiguration<Author>
{
    public void Configure(EntityTypeBuilder<Author> builder)
    {
        builder.ToTable("Authors", t =>
            t.HasCheckConstraint("CK_Authors_BirthYear",
                "[BirthYear] IS NULL OR ([BirthYear] >= 1000 AND [BirthYear] <= 2100)"));

        builder.HasKey(a => a.Id);

        builder.Property(a => a.FullName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.Country).HasMaxLength(100);
        builder.Property(a => a.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(a => a.IsDeleted).HasDefaultValue(false);
        builder.Property(a => a.RowVersion).IsRowVersion();

        // індекс під запит: «пошук автора за прізвищем»
        builder.HasIndex(a => a.FullName).HasDatabaseName("IX_Authors_FullName");

        /* МОДЕЛЬНИЙ (статичний) СІДІНГ — HasData.
           Довідник із ФІКСОВАНИМИ Id: міграція сама стежить, щоб рядки
           з'явилися рівно один раз, тому повторне застосування дублікатів
           не створює. Другий, програмний спосіб сідінгу — у DbInitializer. */
        builder.HasData(
            new { Id = 1, FullName = "Тарас Шевченко",         Country = "Україна", BirthYear = (int?)1814, CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 2, FullName = "Іван Багряний",          Country = "Україна", BirthYear = (int?)1906, CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 3, FullName = "Валер'ян Підмогильний",  Country = "Україна", BirthYear = (int?)1901, CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 4, FullName = "Сергій Жадан",           Country = "Україна", BirthYear = (int?)1974, CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 5, FullName = "Василь Шкляр",           Country = "Україна", BirthYear = (int?)1951, CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 6, FullName = "Ліна Костенко",          Country = "Україна", BirthYear = (int?)1930, CreatedAt = SeedConstants.SeedDate, IsDeleted = false });
    }
}

/// <summary>Конфігурація <see cref="Genre"/> + СТАТИЧНИЙ сідінг довідника.</summary>
public sealed class GenreConfiguration : IEntityTypeConfiguration<Genre>
{
    public void Configure(EntityTypeBuilder<Genre> builder)
    {
        builder.ToTable("Genres");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name).IsRequired().HasMaxLength(100);
        builder.Property(g => g.Slug).IsRequired().HasMaxLength(100);
        builder.Property(g => g.Description).HasMaxLength(500);
        builder.Property(g => g.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(g => g.IsDeleted).HasDefaultValue(false);

        // унікальні бізнес-ключі: назва та slug з URL
        builder.HasIndex(g => g.Name).IsUnique().HasDatabaseName("UQ_Genres_Name");
        builder.HasIndex(g => g.Slug).IsUnique().HasDatabaseName("UQ_Genres_Slug");

        builder.HasData(
            new { Id = 1, Name = "Поезія",           Slug = "poetry",     Description = (string?)"Віршовані твори",                   CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 2, Name = "Історичний роман", Slug = "historical", Description = (string?)"Проза на історичному матеріалі",    CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 3, Name = "Сучасна проза",    Slug = "modern",     Description = (string?)"Українська література XX–XXI ст.",  CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 4, Name = "Класика",          Slug = "classic",    Description = (string?)"Канонічні твори",                   CreatedAt = SeedConstants.SeedDate, IsDeleted = false },
            new { Id = 5, Name = "Публіцистика",     Slug = "nonfiction", Description = (string?)"Нехудожня література",              CreatedAt = SeedConstants.SeedDate, IsDeleted = false });
    }
}

/// <summary>
/// Конфігурація <see cref="BookDetail"/> — зв'язок <b>1:1</b>:
/// первинний ключ є водночас зовнішнім.
/// </summary>
public sealed class BookDetailConfiguration : IEntityTypeConfiguration<BookDetail>
{
    public void Configure(EntityTypeBuilder<BookDetail> builder)
    {
        builder.ToTable("BookDetails", t =>
            t.HasCheckConstraint("CK_BookDetails_PageCount",
                "[PageCount] IS NULL OR [PageCount] > 0"));

        // PK = FK: саме це робить зв'язок 1:1, а не 1:N
        builder.HasKey(d => d.BookId);

        builder.Property(d => d.Description).IsRequired().HasMaxLength(2000);
        builder.Property(d => d.Publisher).HasMaxLength(200);
        builder.Property(d => d.Language).IsRequired().HasMaxLength(50).HasDefaultValue("uk");
        builder.Property(d => d.CoverUrl).HasMaxLength(500);
    }
}

/// <summary>Конфігурація <see cref="BookCopy"/> — зв'язок <b>1:N</b> від книги.</summary>
public sealed class BookCopyConfiguration : IEntityTypeConfiguration<BookCopy>
{
    public void Configure(EntityTypeBuilder<BookCopy> builder)
    {
        builder.ToTable("BookCopies", t =>
            t.HasCheckConstraint("CK_BookCopies_Condition",
                "[Condition] IN ('New', 'Good', 'Worn', 'Damaged', 'Lost')"));

        builder.HasKey(c => c.Id);

        builder.Property(c => c.InventoryNumber).IsRequired().HasMaxLength(40);
        builder.Property(c => c.Shelf).HasMaxLength(40);
        builder.Property(c => c.Condition).IsRequired().HasMaxLength(20).HasDefaultValue("Good");

        // унікальний інвентарний номер — бізнес-ключ примірника
        builder.HasIndex(c => c.InventoryNumber)
               .IsUnique()
               .HasDatabaseName("UQ_BookCopies_InventoryNumber");

        // індекс під запит: «усі примірники книги зі станом»
        builder.HasIndex(c => c.BookId)
               .HasDatabaseName("IX_BookCopies_BookId")
               .IncludeProperties(c => new { c.Condition, c.Shelf });
    }
}

/// <summary>
/// Конфігурація <see cref="BookGenre"/> — зв'язок <b>M:N</b> через ЯВНУ
/// проміжну сутність із власними полями.
/// </summary>
/// <remarks>
/// Автоматичний M:N в EF Core (<c>HasMany().WithMany()</c>) створив би «чисту»
/// таблицю лише з двома зовнішніми ключами й зберегти <c>AddedAt</c>,
/// <c>AddedBy</c>, <c>IsPrimary</c> не дозволив би. Саме через ці поля
/// проміжну сутність описано явно.
/// </remarks>
public sealed class BookGenreConfiguration : IEntityTypeConfiguration<BookGenre>
{
    public void Configure(EntityTypeBuilder<BookGenre> builder)
    {
        builder.ToTable("BookGenres");

        // складений первинний ключ: пара (книга, жанр) не може повторитися
        builder.HasKey(bg => new { bg.BookId, bg.GenreId });

        builder.Property(bg => bg.AddedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(bg => bg.AddedBy).HasMaxLength(100);
        builder.Property(bg => bg.IsPrimary).HasDefaultValue(false);

        builder.HasOne(bg => bg.Book)
               .WithMany(b => b.BookGenres)
               .HasForeignKey(bg => bg.BookId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(bg => bg.Genre)
               .WithMany(g => g.BookGenres)
               .HasForeignKey(bg => bg.GenreId)
               .OnDelete(DeleteBehavior.Restrict);   // жанр-довідник не видаляємо каскадно

        // індекс під запит: «нові книги жанру за період»
        // (LINQ-запит через проміжну сутність, Частина 2.2)
        builder.HasIndex(bg => new { bg.GenreId, bg.AddedAt })
               .HasDatabaseName("IX_BookGenres_GenreId_AddedAt")
               .IsDescending(false, true)
               .IncludeProperties(bg => bg.BookId);
    }
}
