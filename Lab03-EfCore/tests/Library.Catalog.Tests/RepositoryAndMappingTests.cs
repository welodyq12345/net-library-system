using AutoMapper;
using Library.Catalog.Bll.Dtos;
using Library.Catalog.Bll.Mapping;
using Library.Catalog.Bll.Validators;
using Library.Catalog.Dal;
using Library.Catalog.Dal.Repositories;
using Library.Catalog.Dal.Specifications;
using Library.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Library.Catalog.Tests;

/// <summary>
/// Перевірка конфігурації AutoMapper та проєкції сутностей у DTO.
/// </summary>
public class MappingTests
{
    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<CatalogProfile>(),
                                NullLoggerFactory.Instance).CreateMapper();

    [Fact]
    public void Configuration_IsValid()
    {
        var config = new MapperConfiguration(
            cfg => cfg.AddProfile<CatalogProfile>(), NullLoggerFactory.Instance);

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Maps_Book_To_BookDto_WithAuthorAndGenres()
    {
        var mapper = CreateMapper();
        var book = new Book("Кобзар", "978-966-03-5555-1", 3.5m, 1, 1840);
        book.SetDetail(new BookDetail("Опис", "Дніпро", 368));
        book.AddCopy(new BookCopy("INV-0001", "A-01"));

        var dto = mapper.Map<BookDto>(book);

        Assert.Equal("Кобзар", dto.Title);
        Assert.Equal(3.5m, dto.DailyFee);
        Assert.Equal(1, dto.AvailableCopies);
        Assert.NotNull(dto.Detail);
        Assert.Equal("Дніпро", dto.Detail!.Publisher);
        Assert.Single(dto.Copies);
        Assert.Equal("", dto.AuthorName);            // Author не завантажено — порожній рядок, не виняток
    }

    [Fact]
    public void Maps_Author_To_AuthorDto()
    {
        var mapper = CreateMapper();
        var author = new Author("Ліна Костенко", "Україна", 1930);

        var dto = mapper.Map<AuthorDto>(author);

        Assert.Equal("Ліна Костенко", dto.FullName);
        Assert.Equal(1930, dto.BirthYear);
    }
}

/// <summary>
/// Тести репозиторіїв і специфікацій на провайдері InMemory.
/// </summary>
/// <remarks>
/// InMemory не перевіряє SQL-обмеження, тому ним тестуємо саме ЛОГІКУ вибірок
/// (фільтри, сортування, пагінацію, запит через проміжну сутність M:N).
/// Обмеження БД і реальні SQL-запити перевіряє скрипт <c>tests/api-tests.py</c>
/// проти справжнього SQL Server.
/// </remarks>
public class RepositoryTests : IDisposable
{
    private readonly CatalogDbContext _context;

    public RepositoryTests()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"catalog-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new CatalogDbContext(options);
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        var poetry = new Genre("Поезія", "poetry");
        var modern = new Genre("Сучасна проза", "modern");
        _context.Genres.AddRange(poetry, modern);
        await _context.SaveChangesAsync();

        var shevchenko = new Author("Тарас Шевченко", "Україна", 1814);
        var zhadan = new Author("Сергій Жадан", "Україна", 1974);
        _context.Authors.AddRange(shevchenko, zhadan);
        await _context.SaveChangesAsync();

        var kobzar = new Book("Кобзар", "978-966-03-5555-1", 3.50m, shevchenko.Id, 1840);
        kobzar.AddStock(5);
        kobzar.AssignGenre(poetry.Id, isPrimary: true);

        var internat = new Book("Інтернат", "978-966-03-5555-4", 5.00m, zhadan.Id, 2017);
        internat.AssignGenre(modern.Id, isPrimary: true);     // залишок навмисно 0

        var misto = new Book("Місто", "978-966-03-5555-3", 4.25m, zhadan.Id, 1928);
        misto.AddStock(2);
        misto.AssignGenre(modern.Id);

        _context.Books.AddRange(kobzar, internat, misto);
        await _context.SaveChangesAsync();

        // Після сідінгу всі сутності лишаються у ChangeTracker, і EF Core
        // автоматично «склеює» навігації між відстежуваними об'єктами
        // (identity resolution). Тоді Author.Books виявився б заповненим ще
        // ДО Explicit Loading — і тест перевіряв би не те. Очищаємо трекер,
        // щоб кожен тест читав з «чистого» контексту, як це робить застосунок.
        _context.ChangeTracker.Clear();
    }

    public void Dispose() => _context.Dispose();

    // ------------------------------------------------------ Eager Loading ---

    [Fact]
    public async Task BookRepository_GetWithDetails_LoadsRelatedData()
    {
        var repo = new BookRepository(_context);
        var id = _context.Books.First(b => b.Isbn == "978-966-03-5555-1").Id;

        var book = await repo.GetWithDetailsAsync(id);

        Assert.NotNull(book);
        Assert.NotNull(book!.Author);
        Assert.Equal("Тарас Шевченко", book.Author.FullName);
        Assert.Single(book.BookGenres);
    }

    [Fact]
    public async Task BookRepository_ExistsByIsbn_Works()
    {
        var repo = new BookRepository(_context);

        Assert.True(await repo.ExistsByIsbnAsync("978-966-03-5555-1"));
        Assert.False(await repo.ExistsByIsbnAsync("000-000-00-0000-0"));
    }

    // --------------------------------------------------- Explicit Loading ---

    [Fact]
    public async Task AuthorRepository_ExplicitLoading_LoadsBooksOnlyWhenRequested()
    {
        var repo = new AuthorRepository(_context);
        var id = _context.Authors.First(a => a.FullName == "Сергій Жадан").Id;

        var without = await repo.GetWithBooksAsync(id, loadBooks: false);
        Assert.NotNull(without);
        Assert.Empty(without!.Books);

        _context.ChangeTracker.Clear();

        var with = await repo.GetWithBooksAsync(id, loadBooks: true);
        Assert.NotNull(with);
        Assert.Equal(2, with!.Books.Count);
    }

    // ------------------------------------- LINQ через проміжну сутність M:N ---

    [Fact]
    public async Task BookGenreRepository_GetRecentlyAdded_FiltersByJunctionOwnField()
    {
        var repo = new BookGenreRepository(_context);

        var recent = await repo.GetRecentlyAddedAsync("Поезія", DateTime.UtcNow.AddDays(-1));
        Assert.Single(recent);
        Assert.Equal("Кобзар", recent[0].Title);

        // той самий запит, але з майбутньої дати -> порожньо (працює фільтр AddedAt)
        var none = await repo.GetRecentlyAddedAsync("Поезія", DateTime.UtcNow.AddDays(1));
        Assert.Empty(none);
    }

    [Fact]
    public async Task BookGenreRepository_GetGenreStats_CountsBooksPerGenre()
    {
        var repo = new BookGenreRepository(_context);

        var stats = await repo.GetGenreStatsAsync();

        Assert.Equal(2, stats.Count);
        Assert.Equal(2, stats.Single(s => s.GenreName == "Сучасна проза").BookCount);
        Assert.Equal(1, stats.Single(s => s.GenreName == "Поезія").BookCount);
    }

    // ------------------------------------------------ Specification pattern ---

    [Fact]
    public async Task Specification_Filters_BySearchTerm()
    {
        var repo = new BookRepository(_context);

        var items = await repo.ListAsync(new BooksFilterSpec(new BookQuery(Search: "Кобзар")));

        Assert.Single(items);
        Assert.Equal("Кобзар", items[0].Title);
    }

    [Fact]
    public async Task Specification_Filters_ByFeeRange()
    {
        var repo = new BookRepository(_context);

        var items = await repo.ListAsync(new BooksFilterSpec(new BookQuery(MinFee: 4m, MaxFee: 5m)));

        Assert.Equal(2, items.Count);
        Assert.All(items, b => Assert.InRange(b.DailyFee, 4m, 5m));
    }

    [Fact]
    public async Task Specification_Sorts_ByFeeDescending()
    {
        var repo = new BookRepository(_context);

        var items = await repo.ListAsync(
            new BooksFilterSpec(new BookQuery(SortBy: "fee", SortDir: "desc")));

        var fees = items.Select(b => b.DailyFee).ToList();
        Assert.Equal(fees.OrderByDescending(f => f).ToList(), fees);
    }

    /// <summary>
    /// <c>CountAsync</c> має рахувати ВСІ записи за критеріями, ігноруючи
    /// <c>Skip</c>/<c>Take</c> — інакше totalCount показував би розмір сторінки.
    /// </summary>
    [Fact]
    public async Task Specification_Count_IgnoresPaging()
    {
        var repo = new BookRepository(_context);
        var query = new BookQuery(Page: 1, PageSize: 2);

        var page = await repo.ListAsync(new BooksFilterSpec(query, applyPaging: true));
        var total = await repo.CountAsync(new BooksFilterSpec(query, applyPaging: false));

        Assert.Equal(2, page.Count);     // на сторінці
        Assert.Equal(3, total);          // усього в базі
    }

    /// <summary>
    /// ДРУГА специфікація працює через той самий <c>ListAsync</c>
    /// без жодних змін у репозиторії.
    /// </summary>
    [Fact]
    public async Task SecondSpecification_WorksThroughSameRepositoryMethod()
    {
        var repo = new BookRepository(_context);

        var outOfStock = await repo.ListAsync(new OutOfStockBooksSpec());

        Assert.Single(outOfStock);
        Assert.Equal("Інтернат", outOfStock[0].Title);
    }

    [Fact]
    public async Task ThirdSpecification_FiltersByGenreSlug()
    {
        var repo = new BookRepository(_context);

        var items = await repo.ListAsync(new BooksByGenreSlugSpec("modern"));

        Assert.Equal(2, items.Count);
    }

    // ---------------------------------------------- ідемпотентність сідінгу ---

    [Fact]
    public async Task Seeding_IsIdempotent()
    {
        var before = await _context.Books.CountAsync();

        await DbInitializer.SeedAsync(_context);     // книги вже є -> має вийти одразу
        await DbInitializer.SeedAsync(_context);

        Assert.Equal(before, await _context.Books.CountAsync());
    }
}

/// <summary>Тести валідаторів FluentValidation — без бази й без HTTP.</summary>
public class ValidatorTests
{
    [Theory]
    [InlineData("978-966-03-5555-1", true)]
    [InlineData("1234567890", false)]
    [InlineData("978-966-03-5555", false)]
    [InlineData("", false)]
    public void CreateBookDtoValidator_ChecksIsbnFormat(string isbn, bool expectedValid)
    {
        var validator = new CreateBookDtoValidator();
        var dto = new CreateBookDto { Title = "Назва", Isbn = isbn, DailyFee = 5m, AuthorId = 1 };

        var result = validator.Validate(dto);

        Assert.Equal(expectedValid, result.Errors.All(e => e.PropertyName != nameof(dto.Isbn)));
    }

    [Fact]
    public void CreateBookDtoValidator_RequiresDescription_WhenPublisherGiven()
    {
        var validator = new CreateBookDtoValidator();
        var dto = new CreateBookDto
        {
            Title = "Назва", Isbn = "978-966-03-5555-1", DailyFee = 5m,
            AuthorId = 1, Publisher = "Дніпро"       // опису немає
        };

        var result = validator.Validate(dto);

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(dto.Description));
    }

    [Fact]
    public void CreateBookDtoValidator_RejectsDuplicateGenres()
    {
        var validator = new CreateBookDtoValidator();
        var dto = new CreateBookDto
        {
            Title = "Назва", Isbn = "978-966-03-5555-1", DailyFee = 5m,
            AuthorId = 1, GenreIds = [1, 1, 2]
        };

        var result = validator.Validate(dto);

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(dto.GenreIds));
    }

    [Theory]
    [InlineData("title", true)]
    [InlineData("fee", true)]
    [InlineData("drop_table", false)]
    [InlineData("'; DELETE FROM Books --", false)]
    public void BookQueryValidator_AllowsOnlyWhitelistedSortFields(string sortBy, bool expectedValid)
    {
        var validator = new BookQueryValidator();

        var result = validator.Validate(new BookQuery(SortBy: sortBy));

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void BookQueryValidator_RejectsPageSizeAboveLimit()
    {
        var validator = new BookQueryValidator();

        var result = validator.Validate(new BookQuery(PageSize: 5000));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void BookQueryValidator_RejectsInvertedFeeRange()
    {
        var validator = new BookQueryValidator();

        var result = validator.Validate(new BookQuery(MinFee: 50m, MaxFee: 10m));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AddCopyDtoValidator_RejectsNonLatinInventoryNumber()
    {
        var validator = new AddCopyDtoValidator();

        var result = validator.Validate(new AddCopyDto { InventoryNumber = "інв-1", Condition = "Good" });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AddCopyDtoValidator_RejectsFutureAcquisitionDate()
    {
        var validator = new AddCopyDtoValidator();

        var result = validator.Validate(new AddCopyDto
        {
            InventoryNumber = "INV-1",
            Condition = "Good",
            AcquiredAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10))
        });

        Assert.False(result.IsValid);
    }
}

/// <summary>Тести контракту посторінкової відповіді.</summary>
public class PagedResultTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(6, 5, 2)]
    [InlineData(10, 5, 2)]
    [InlineData(11, 5, 3)]
    public void TotalPages_IsCeilingOfDivision(int total, int pageSize, int expected)
    {
        var result = new PagedResult<string>([], total, 1, pageSize);
        Assert.Equal(expected, result.TotalPages);
    }

    [Fact]
    public void HasNext_And_HasPrevious_ReflectPosition()
    {
        var first = new PagedResult<string>([], 20, 1, 5);
        Assert.True(first.HasNext);
        Assert.False(first.HasPrevious);

        var middle = new PagedResult<string>([], 20, 2, 5);
        Assert.True(middle.HasNext);
        Assert.True(middle.HasPrevious);

        var last = new PagedResult<string>([], 20, 4, 5);
        Assert.False(last.HasNext);
        Assert.True(last.HasPrevious);
    }

    [Theory]
    [InlineData(0, BookQuery.DefaultPageSize)]
    [InlineData(-5, BookQuery.DefaultPageSize)]
    [InlineData(50, 50)]
    [InlineData(5000, BookQuery.MaxPageSize)]
    public void BookQuery_SafePageSize_IsClamped(int given, int expected)
        => Assert.Equal(expected, new BookQuery(PageSize: given).SafePageSize);
}
