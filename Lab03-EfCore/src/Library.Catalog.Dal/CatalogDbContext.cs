using Library.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Library.Catalog.Dal;

/// <summary>
/// Контекст бази даних Каталогу (Проєкт №2 з Лаб. 01), побудований
/// підходом <b>Code-First</b>: схема генерується з моделей міграціями.
/// </summary>
/// <remarks>
/// Усі конфігурації сутностей підключаються ЦЕНТРАЛІЗОВАНО одним викликом
/// <see cref="ModelBuilder.ApplyConfigurationsFromAssembly"/> — це гарантує
/// однаковий підхід до всіх моделей і не дає забути нову конфігурацію.
/// </remarks>
public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<BookDetail> BookDetails => Set<BookDetail>();
    public DbSet<BookCopy> BookCopies => Set<BookCopy>();
    public DbSet<BookGenre> BookGenres => Set<BookGenre>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // усі IEntityTypeConfiguration<T> зі збірки DAL — одним рядком
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
