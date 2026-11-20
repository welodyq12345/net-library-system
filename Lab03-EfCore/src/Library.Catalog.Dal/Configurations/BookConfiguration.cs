using Library.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Library.Catalog.Dal.Configurations;

/// <summary>
/// Конфігурація <see cref="Book"/> через Fluent API.
/// </summary>
/// <remarks>
/// Окремий клас <see cref="IEntityTypeConfiguration{TEntity}"/> на кожну
/// сутність тримає логіку конфігурації відокремленою й не «захаращує» класи
/// сутностей атрибутами. Усі конфігурації підключаються централізовано в
/// <c>OnModelCreating</c> одним викликом
/// <c>ApplyConfigurationsFromAssembly</c>.
/// </remarks>
public sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder)
    {
        builder.ToTable("Books", t =>
        {
            t.HasCheckConstraint("CK_Books_DailyFee", "[DailyFee] > 0");
            t.HasCheckConstraint("CK_Books_AvailableCopies", "[AvailableCopies] >= 0");
            t.HasCheckConstraint("CK_Books_PublishedYear",
                "[PublishedYear] IS NULL OR ([PublishedYear] >= 1400 AND [PublishedYear] <= 2100)");
        });

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Title).IsRequired().HasMaxLength(300);
        builder.Property(b => b.Isbn).IsRequired().HasMaxLength(20);
        builder.Property(b => b.DailyFee).HasColumnType("decimal(18,2)");
        builder.Property(b => b.AvailableCopies).HasDefaultValue(0);
        builder.Property(b => b.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(b => b.IsDeleted).HasDefaultValue(false);

        // оптимістична конкурентність: SQL Server сам оновлює ROWVERSION
        builder.Property(b => b.RowVersion).IsRowVersion();

        // --- індекси (кожен під конкретний запит) ---

        // унікальний бізнес-ключ; повторне створення -> 409 Conflict
        builder.HasIndex(b => b.Isbn).IsUnique().HasDatabaseName("UQ_Books_Isbn");

        // «список книг із пошуком за назвою» — покриваючий індекс для пагінації
        builder.HasIndex(b => b.Title)
               .HasDatabaseName("IX_Books_Title")
               .IncludeProperties(b => new { b.DailyFee, b.AvailableCopies, b.AuthorId });

        // «усі книги автора» — під JOIN з Authors
        builder.HasIndex(b => b.AuthorId).HasDatabaseName("IX_Books_AuthorId");

        // «наявні в каталозі книги» — фільтрований індекс лише для «живих» записів
        builder.HasIndex(b => b.Title)
               .HasDatabaseName("IX_Books_Active")
               .HasFilter("[IsDeleted] = 0");

        // --- зв'язки ---

        // N:1 до Authors (з боку автора — 1:N). БЕЗ каскаду:
        // автора з книгами видаляти не можна.
        builder.HasOne(b => b.Author)
               .WithMany(a => a.Books)
               .HasForeignKey(b => b.AuthorId)
               .OnDelete(DeleteBehavior.Restrict);

        // 1:1 до BookDetails. PK залежної сутності є водночас FK,
        // тому двох описів на одну книгу фізично бути не може.
        builder.HasOne(b => b.Detail)
               .WithOne()
               .HasForeignKey<BookDetail>(d => d.BookId)
               .OnDelete(DeleteBehavior.Cascade);

        // 1:N до BookCopies. Каскад доречний: примірник не існує без книги.
        builder.HasMany(b => b.Copies)
               .WithOne()
               .HasForeignKey(c => c.BookId)
               .OnDelete(DeleteBehavior.Cascade);

        // M:N до Genres через BookGenres налаштовано в BookGenreConfiguration

        // навігаційні колекції заповнює EF Core через backing field
        builder.Navigation(b => b.Copies).UsePropertyAccessMode(PropertyAccessMode.Property);
        builder.Navigation(b => b.BookGenres).UsePropertyAccessMode(PropertyAccessMode.Property);
        builder.Navigation(b => b.Detail).UsePropertyAccessMode(PropertyAccessMode.Property);
    }
}
