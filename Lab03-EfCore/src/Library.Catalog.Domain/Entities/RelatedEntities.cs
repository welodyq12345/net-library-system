using Library.Catalog.Domain.Exceptions;

namespace Library.Catalog.Domain.Entities;

/// <summary>Автор. Зв'язок <b>1:N</b> до <see cref="Book"/>.</summary>
public class Author
{
    public int Id { get; private set; }
    public string FullName { get; private set; } = "";
    public string? Country { get; private set; }
    public int? BirthYear { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public bool IsDeleted { get; private set; }
    public byte[]? RowVersion { get; private set; }

    public List<Book> Books { get; private set; } = [];

    private Author() { }

    public Author(string fullName, string? country = null, int? birthYear = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Author FullName must not be empty");
        if (birthYear is < 1000 or > 2100)
            throw new DomainException("BirthYear must be between 1000 and 2100");

        FullName = fullName.Trim();
        Country = country?.Trim();
        BirthYear = birthYear;
        CreatedAt = DateTime.UtcNow;
    }

    public void Rename(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Author FullName must not be empty");

        FullName = fullName.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkDeleted()
    {
        if (Books.Count > 0)
            throw new DomainException(
                $"Author '{FullName}' still has {Books.Count} book(s) and cannot be deleted");

        IsDeleted = true;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Опис книги. Зв'язок <b>1:1</b> до <see cref="Book"/>:
/// первинний ключ цієї сутності є водночас зовнішнім ключем.
/// </summary>
public class BookDetail
{
    public long BookId { get; private set; }
    public string Description { get; private set; } = "";
    public string? Publisher { get; private set; }
    public int? PageCount { get; private set; }
    public string Language { get; private set; } = "uk";
    public string? CoverUrl { get; private set; }

    private BookDetail() { }

    public BookDetail(string description, string? publisher = null,
                      int? pageCount = null, string language = "uk", string? coverUrl = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new DomainException("Description must not be empty");
        if (pageCount is <= 0)
            throw new DomainException("PageCount must be positive");

        Description = description.Trim();
        Publisher = publisher?.Trim();
        PageCount = pageCount;
        Language = string.IsNullOrWhiteSpace(language) ? "uk" : language.Trim();
        CoverUrl = coverUrl;
    }

    public void Update(string description, string? publisher, int? pageCount)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new DomainException("Description must not be empty");
        if (pageCount is <= 0)
            throw new DomainException("PageCount must be positive");

        Description = description.Trim();
        Publisher = publisher?.Trim();
        PageCount = pageCount;
    }
}

/// <summary>Стан фізичного примірника.</summary>
public static class CopyCondition
{
    public const string New = "New";
    public const string Good = "Good";
    public const string Worn = "Worn";
    public const string Damaged = "Damaged";
    public const string Lost = "Lost";

    public static readonly IReadOnlyCollection<string> All =
        new[] { New, Good, Worn, Damaged, Lost };

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

/// <summary>Фізичний примірник книги на полиці. Зв'язок <b>1:N</b> від <see cref="Book"/>.</summary>
public class BookCopy
{
    public long Id { get; private set; }
    public long BookId { get; private set; }
    public string InventoryNumber { get; private set; } = "";
    public string? Shelf { get; private set; }
    public string Condition { get; private set; } = CopyCondition.Good;
    public DateOnly? AcquiredAt { get; private set; }

    private BookCopy() { }

    public BookCopy(string inventoryNumber, string? shelf = null,
                    string condition = CopyCondition.Good, DateOnly? acquiredAt = null)
    {
        if (string.IsNullOrWhiteSpace(inventoryNumber))
            throw new DomainException("InventoryNumber must not be empty");
        if (!CopyCondition.IsValid(condition))
            throw new DomainException(
                $"Condition must be one of: {string.Join(", ", CopyCondition.All)}");

        InventoryNumber = inventoryNumber.Trim();
        Shelf = shelf?.Trim();
        Condition = condition;
        AcquiredAt = acquiredAt;
    }

    public void ChangeCondition(string condition)
    {
        if (!CopyCondition.IsValid(condition))
            throw new DomainException(
                $"Condition must be one of: {string.Join(", ", CopyCondition.All)}");

        Condition = condition;
    }

    public void MoveToShelf(string? shelf) => Shelf = shelf?.Trim();
}

/// <summary>Жанр (довідник). Зв'язок <b>M:N</b> до <see cref="Book"/> через <see cref="BookGenre"/>.</summary>
public class Genre
{
    public int Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string? Description { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public bool IsDeleted { get; private set; }

    public List<BookGenre> BookGenres { get; private set; } = [];

    private Genre() { }

    public Genre(string name, string slug, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Genre Name must not be empty");
        if (string.IsNullOrWhiteSpace(slug))
            throw new DomainException("Genre Slug must not be empty");

        Name = name.Trim();
        Slug = slug.Trim().ToLowerInvariant();
        Description = description?.Trim();
        CreatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Проміжна сутність зв'язку <b>M:N</b> між <see cref="Book"/> і <see cref="Genre"/>
/// з <b>власними полями</b> <see cref="AddedAt"/>, <see cref="AddedBy"/>,
/// <see cref="IsPrimary"/>.
/// </summary>
/// <remarks>
/// Саме через ці поля зв'язок описано ЯВНОЮ сутністю: автоматичний M:N в EF Core
/// (<c>HasMany().WithMany()</c>) створив би «чисту» таблицю лише з двома
/// зовнішніми ключами й зберегти додаткові поля не дозволив би.
/// Поле <see cref="AddedAt"/> використовується в LINQ-запиті «нові книги жанру
/// за період» (Частина 2.2 Лаб. 03).
/// </remarks>
public class BookGenre
{
    public long BookId { get; private set; }
    public int GenreId { get; private set; }
    public DateTime AddedAt { get; private set; }
    public string? AddedBy { get; private set; }
    public bool IsPrimary { get; private set; }

    public Book Book { get; private set; } = null!;
    public Genre Genre { get; private set; } = null!;

    private BookGenre() { }

    public BookGenre(int genreId, bool isPrimary = false, string? addedBy = null)
    {
        if (genreId <= 0)
            throw new DomainException("GenreId must be a positive value");

        GenreId = genreId;
        IsPrimary = isPrimary;
        AddedBy = addedBy;
        AddedAt = DateTime.UtcNow;
    }

    public void MarkPrimary() => IsPrimary = true;

    public void ClearPrimary() => IsPrimary = false;
}
