using Library.Catalog.Domain.Exceptions;

namespace Library.Catalog.Domain.Entities;

/// <summary>
/// Книга — коренева сутність каталогу (аналог <c>CatalogItem</c> в eShop).
/// </summary>
/// <remarks>
/// <para>
/// <b>Бізнес-правила реалізовано МЕТОДАМИ сутності, а не відкритими setter-ами.</b>
/// Усі властивості мають <c>private set</c>, тому стан неможливо зіпсувати
/// «збоку»: ціну перевіряє конструктор і <see cref="ChangeDailyFee"/>, залишок
/// змінюють лише <see cref="RemoveStock"/> та <see cref="AddStock"/>. Так само
/// побудовано <c>CatalogItem</c> у референсному застосунку Microsoft eShop
/// (методи <c>RemoveStock</c> / <c>AddStock</c>).
/// </para>
/// <para>
/// <c>Id</c> цієї сутності — <b>власник ідентифікатора книги в усій системі</b>:
/// саме він дублюється в <c>LibraryLendingDb.Books</c> (Проєкт №1) та в
/// <c>reviews.bookId</c> (MongoDB, Проєкт №3) — див. таблицю політики
/// дублювання в Лаб. 01.
/// </para>
/// </remarks>
public class Book
{
    public long Id { get; private set; }
    public string Title { get; private set; } = "";
    public string Isbn { get; private set; } = "";
    public decimal DailyFee { get; private set; }
    public int AvailableCopies { get; private set; }
    public int? PublishedYear { get; private set; }
    public int AuthorId { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public bool IsDeleted { get; private set; }

    /// <summary>Токен оптимістичної конкурентності (<c>ROWVERSION</c> у SQL Server).</summary>
    public byte[]? RowVersion { get; private set; }

    // --- навігаційні властивості ---

    /// <summary>Автор книги. Зв'язок N:1 (з боку автора — 1:N).</summary>
    public Author Author { get; private set; } = null!;

    /// <summary>Опис книги. Зв'язок <b>1:1</b> (PK залежної таблиці = FK).</summary>
    public BookDetail? Detail { get; private set; }

    /// <summary>Фізичні примірники. Зв'язок <b>1:N</b>.</summary>
    public List<BookCopy> Copies { get; private set; } = [];

    /// <summary>Жанри через проміжну сутність. Зв'язок <b>M:N</b> із власним полем.</summary>
    public List<BookGenre> BookGenres { get; private set; } = [];

    /// <summary>Конструктор для EF Core (матеріалізація з БД).</summary>
    private Book() { }

    public Book(string title, string isbn, decimal dailyFee, int authorId, int? publishedYear = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("Title must not be empty");
        if (string.IsNullOrWhiteSpace(isbn))
            throw new DomainException("ISBN must not be empty");
        if (dailyFee <= 0)
            throw new DomainException("DailyFee must be positive");
        if (authorId <= 0)
            throw new DomainException("AuthorId must be a positive value");
        if (publishedYear is < 1400 or > 2100)
            throw new DomainException("PublishedYear must be between 1400 and 2100");

        Title = title.Trim();
        Isbn = isbn.Trim();
        DailyFee = dailyFee;
        AuthorId = authorId;
        PublishedYear = publishedYear;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Списання примірників. Інваріант перевіряє САМА сутність:
    /// не можна зняти більше, ніж доступно.
    /// </summary>
    public void RemoveStock(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be positive");
        if (AvailableCopies < quantity)
            throw new DomainException(
                $"Only {AvailableCopies} copies of '{Title}' are available, {quantity} requested");

        AvailableCopies -= quantity;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Надходження примірників.</summary>
    public void AddStock(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be positive");

        AvailableCopies += quantity;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Зміна тарифу. Нуль і відʼємні значення неприпустимі.</summary>
    public void ChangeDailyFee(decimal newFee)
    {
        if (newFee <= 0)
            throw new DomainException("DailyFee must be positive");

        DailyFee = newFee;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Rename(string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle))
            throw new DomainException("Title must not be empty");

        Title = newTitle.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Встановлює або замінює опис книги (зв'язок 1:1).</summary>
    public void SetDetail(BookDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        Detail = detail;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Додає фізичний примірник (зв'язок 1:N) і збільшує залишок.</summary>
    public void AddCopy(BookCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        if (Copies.Any(c => c.InventoryNumber == copy.InventoryNumber))
            throw new DomainException(
                $"Copy with inventory number '{copy.InventoryNumber}' already exists for this book");

        Copies.Add(copy);
        AddStock(1);
    }

    /// <summary>
    /// Призначає книзі жанр через проміжну сутність M:N.
    /// Повторне призначення того самого жанру — порушення інваріанту.
    /// </summary>
    public void AssignGenre(int genreId, bool isPrimary = false, string? addedBy = null)
    {
        if (BookGenres.Any(bg => bg.GenreId == genreId))
            throw new DomainException($"Genre {genreId} is already assigned to book '{Title}'");

        if (isPrimary)
            foreach (var existing in BookGenres)
                existing.ClearPrimary();     // головний жанр може бути лише один

        BookGenres.Add(new BookGenre(genreId, isPrimary, addedBy));
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>М'яке видалення: запис лишається в базі для цілісності історії.</summary>
    public void MarkDeleted()
    {
        IsDeleted = true;
        UpdatedAt = DateTime.UtcNow;
    }
}
