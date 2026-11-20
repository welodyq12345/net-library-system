using Library.Reviews.Domain.Common;
using Library.Reviews.Domain.Exceptions;

namespace Library.Reviews.Domain.ValueObjects;

/// <summary>
/// Автор відгуку або коментаря — <b>вбудований об'єкт</b> у документі MongoDB
/// (поле <c>author</c> зі схеми Лаб. 01).
/// </summary>
/// <remarks>
/// Незмінний: обидві властивості мають лише getter, валідація — в конструкторі.
/// <see cref="ReaderId"/> може бути <c>null</c> — це анонімний відгук
/// (саме такий документ є в seed-даних Лаб. 01).
/// Аналог <c>Address</c> в eShop.
/// </remarks>
public sealed class Author : ValueObject
{
    /// <summary>Логічне посилання на <c>Readers.Id</c> у БД Видачі. Без FK.</summary>
    public long? ReaderId { get; }

    /// <summary>Імʼя, під яким опубліковано відгук. Знімок на момент публікації.</summary>
    public string DisplayName { get; }

    public Author(long? readerId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("DisplayName is required");
        if (displayName.Length > 100)
            throw new DomainException("DisplayName must not exceed 100 characters");
        if (readerId is <= 0)
            throw new DomainException("ReaderId must be a positive value when provided");

        ReaderId = readerId;
        DisplayName = displayName.Trim();
    }

    /// <summary>Анонімний автор — без посилання на читача.</summary>
    public static Author Anonymous(string displayName = "Анонім") => new(null, displayName);

    public bool IsAnonymous => ReaderId is null;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return ReaderId;
        yield return DisplayName;
    }

    public override string ToString() => IsAnonymous ? DisplayName : $"{DisplayName} (#{ReaderId})";
}

/// <summary>
/// Оцінка від 1 до 5. Value Object, а не <c>int</c>: правило «1..5» живе
/// в одному місці й не може бути порушене десь у коді.
/// </summary>
public sealed class Rating : ValueObject
{
    public const int Min = 1;
    public const int Max = 5;

    public int Value { get; }

    public Rating(int value)
    {
        if (value is < Min or > Max)
            throw new DomainException($"Rating must be between {Min} and {Max}");

        Value = value;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public static implicit operator int(Rating rating) => rating.Value;

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Посилання на книгу з контексту Каталогу разом із <b>денормалізованою</b>
/// назвою.
/// </summary>
/// <remarks>
/// Це прямий наслідок таблиці політики дублювання з Лаб. 01: контекст Відгуків
/// зберігає лише <see cref="BookId"/> (логічне посилання, без FOREIGN KEY) і
/// копію назви для читання, щоб показати список відгуків без жодного звернення
/// до сервісу Каталогу. Назва оновлюється подіями з Каталогу (Лаб. 08).
/// </remarks>
public sealed class BookRef : ValueObject
{
    /// <summary>Той самий Id, що й <c>Books.Id</c> у БД Каталогу. Власник — Каталог.</summary>
    public long BookId { get; }

    /// <summary>Денормалізована назва книги — копія для читання.</summary>
    public string BookTitle { get; }

    public BookRef(long bookId, string bookTitle)
    {
        if (bookId <= 0)
            throw new DomainException("BookId must be a positive value");
        if (string.IsNullOrWhiteSpace(bookTitle))
            throw new DomainException("BookTitle is required");

        BookId = bookId;
        BookTitle = bookTitle.Trim();
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return BookId;
        yield return BookTitle;
    }

    public override string ToString() => $"{BookTitle} (#{BookId})";
}
