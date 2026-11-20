using Library.Catalog.Domain.Entities;
using Library.Catalog.Domain.Exceptions;

namespace Library.Catalog.Tests;

/// <summary>
/// Тести інваріантів сутностей — головна відмінність Лаб. 03 від Лаб. 02:
/// бізнес-правила реалізовано МЕТОДАМИ сутності, а не відкритими setter-ами,
/// тому їх можна перевірити взагалі без бази даних і без моків.
/// </summary>
public class BookEntityTests
{
    private static Book NewBook(decimal fee = 10m) =>
        new("Кобзар", "978-966-03-5555-1", fee, authorId: 1, publishedYear: 1840);

    // ------------------------------------------------------- конструктор ---

    [Fact]
    public void Constructor_Sets_Fields_AndTrimsStrings()
    {
        var book = new Book("  Кобзар  ", " 978-966-03-5555-1 ", 3.5m, 1, 1840);

        Assert.Equal("Кобзар", book.Title);
        Assert.Equal("978-966-03-5555-1", book.Isbn);
        Assert.Equal(3.5m, book.DailyFee);
        Assert.Equal(0, book.AvailableCopies);
        Assert.False(book.IsDeleted);
        Assert.NotEqual(default, book.CreatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public void Constructor_Rejects_NonPositiveFee(decimal fee)
    {
        var ex = Assert.Throws<DomainException>(
            () => new Book("Кобзар", "978-966-03-5555-1", fee, 1));

        Assert.Contains("DailyFee", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_Rejects_EmptyTitle(string title)
        => Assert.Throws<DomainException>(() => new Book(title, "978-966-03-5555-1", 5m, 1));

    [Theory]
    [InlineData(1399)]
    [InlineData(2101)]
    public void Constructor_Rejects_ImpossiblePublishedYear(int year)
        => Assert.Throws<DomainException>(() => new Book("Кобзар", "978-966-03-5555-1", 5m, 1, year));

    // ------------------------------------------------------------ залишок ---

    [Fact]
    public void AddStock_IncreasesAvailableCopies()
    {
        var book = NewBook();

        book.AddStock(5);
        book.AddStock(3);

        Assert.Equal(8, book.AvailableCopies);
        Assert.NotNull(book.UpdatedAt);
    }

    [Fact]
    public void RemoveStock_DecreasesAvailableCopies()
    {
        var book = NewBook();
        book.AddStock(10);

        book.RemoveStock(4);

        Assert.Equal(6, book.AvailableCopies);
    }

    /// <summary>
    /// КЛЮЧОВИЙ інваріант: не можна списати більше, ніж доступно.
    /// Саме так побудовано <c>CatalogItem.RemoveStock</c> в eShop.
    /// </summary>
    [Fact]
    public void RemoveStock_Throws_WhenNotEnoughCopies()
    {
        var book = NewBook();
        book.AddStock(2);

        var ex = Assert.Throws<DomainException>(() => book.RemoveStock(5));

        Assert.Contains("Only 2 copies", ex.Message);
        Assert.Equal(2, book.AvailableCopies);      // стан НЕ змінився
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RemoveStock_Throws_OnNonPositiveQuantity(int quantity)
    {
        var book = NewBook();
        book.AddStock(10);

        Assert.Throws<DomainException>(() => book.RemoveStock(quantity));
        Assert.Equal(10, book.AvailableCopies);
    }

    // --------------------------------------------------------------- тариф ---

    [Fact]
    public void ChangeDailyFee_Updates_Fee()
    {
        var book = NewBook(10m);

        book.ChangeDailyFee(12.75m);

        Assert.Equal(12.75m, book.DailyFee);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ChangeDailyFee_Throws_OnNonPositive(decimal fee)
    {
        var book = NewBook(10m);

        Assert.Throws<DomainException>(() => book.ChangeDailyFee(fee));
        Assert.Equal(10m, book.DailyFee);           // стан НЕ змінився
    }

    // ----------------------------------------------------------- примірники ---

    [Fact]
    public void AddCopy_AddsCopy_AndIncreasesStock()
    {
        var book = NewBook();

        book.AddCopy(new BookCopy("INV-0001", "A-01"));

        Assert.Single(book.Copies);
        Assert.Equal(1, book.AvailableCopies);      // залишок узгоджений із примірниками
    }

    /// <summary>Інвентарний номер унікальний у межах книги — це інваріант сутності.</summary>
    [Fact]
    public void AddCopy_Throws_OnDuplicateInventoryNumber()
    {
        var book = NewBook();
        book.AddCopy(new BookCopy("INV-0001"));

        var ex = Assert.Throws<DomainException>(() => book.AddCopy(new BookCopy("INV-0001")));

        Assert.Contains("INV-0001", ex.Message);
        Assert.Single(book.Copies);
        Assert.Equal(1, book.AvailableCopies);
    }

    // ---------------------------------------------------------------- жанри ---

    [Fact]
    public void AssignGenre_AddsJunctionEntity_WithOwnFields()
    {
        var book = NewBook();

        book.AssignGenre(genreId: 1, isPrimary: true, addedBy: "test");

        var link = Assert.Single(book.BookGenres);
        Assert.Equal(1, link.GenreId);
        Assert.True(link.IsPrimary);
        Assert.Equal("test", link.AddedBy);
        Assert.NotEqual(default, link.AddedAt);     // власне поле M:N
    }

    [Fact]
    public void AssignGenre_Throws_OnDuplicateGenre()
    {
        var book = NewBook();
        book.AssignGenre(1);

        Assert.Throws<DomainException>(() => book.AssignGenre(1));
        Assert.Single(book.BookGenres);
    }

    /// <summary>Головний жанр може бути лише один.</summary>
    [Fact]
    public void AssignGenre_AsPrimary_ClearsPreviousPrimary()
    {
        var book = NewBook();
        book.AssignGenre(1, isPrimary: true);
        book.AssignGenre(2, isPrimary: true);

        Assert.Single(book.BookGenres, bg => bg.IsPrimary);
        Assert.True(book.BookGenres.Single(bg => bg.GenreId == 2).IsPrimary);
        Assert.False(book.BookGenres.Single(bg => bg.GenreId == 1).IsPrimary);
    }

    // ------------------------------------------------------- м'яке видалення ---

    [Fact]
    public void MarkDeleted_SetsFlag()
    {
        var book = NewBook();

        book.MarkDeleted();

        Assert.True(book.IsDeleted);
    }
}

public class AuthorEntityTests
{
    [Fact]
    public void Constructor_Rejects_EmptyName()
        => Assert.Throws<DomainException>(() => new Author("  "));

    [Theory]
    [InlineData(999)]
    [InlineData(2101)]
    public void Constructor_Rejects_ImpossibleBirthYear(int year)
        => Assert.Throws<DomainException>(() => new Author("Тарас Шевченко", "Україна", year));

    [Fact]
    public void MarkDeleted_Succeeds_WhenNoBooks()
    {
        var author = new Author("Тарас Шевченко");

        author.MarkDeleted();

        Assert.True(author.IsDeleted);
    }

    /// <summary>
    /// Інваріант: автора з книгами видалити не можна.
    /// Саме цей виняток сервіс перетворює на HTTP 409.
    /// </summary>
    [Fact]
    public void MarkDeleted_Throws_WhenAuthorStillHasBooks()
    {
        var author = new Author("Тарас Шевченко");
        author.Books.Add(new Book("Кобзар", "978-966-03-5555-1", 3.5m, 1));

        var ex = Assert.Throws<DomainException>(() => author.MarkDeleted());

        Assert.Contains("cannot be deleted", ex.Message);
        Assert.False(author.IsDeleted);
    }
}

public class BookCopyEntityTests
{
    [Fact]
    public void Constructor_Rejects_EmptyInventoryNumber()
        => Assert.Throws<DomainException>(() => new BookCopy(""));

    [Fact]
    public void Constructor_Rejects_UnknownCondition()
        => Assert.Throws<DomainException>(() => new BookCopy("INV-1", condition: "Броньований"));

    [Theory]
    [InlineData("New")]
    [InlineData("Good")]
    [InlineData("Worn")]
    [InlineData("Damaged")]
    [InlineData("Lost")]
    public void Constructor_Accepts_AllKnownConditions(string condition)
    {
        var copy = new BookCopy("INV-1", condition: condition);
        Assert.Equal(condition, copy.Condition);
    }

    [Fact]
    public void ChangeCondition_Throws_OnUnknownValue()
    {
        var copy = new BookCopy("INV-1");

        Assert.Throws<DomainException>(() => copy.ChangeCondition("Невідомо"));
        Assert.Equal("Good", copy.Condition);
    }
}

public class BookDetailEntityTests
{
    [Fact]
    public void Constructor_Rejects_EmptyDescription()
        => Assert.Throws<DomainException>(() => new BookDetail("   "));

    [Fact]
    public void Constructor_Rejects_NonPositivePageCount()
        => Assert.Throws<DomainException>(() => new BookDetail("Опис", pageCount: 0));

    [Fact]
    public void Constructor_Defaults_LanguageToUk()
    {
        var detail = new BookDetail("Опис");
        Assert.Equal("uk", detail.Language);
    }
}
