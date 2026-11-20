using Library.Reviews.Domain.Common;
using Library.Reviews.Domain.Exceptions;
using Library.Reviews.Domain.ValueObjects;
using MongoDB.Bson;

namespace Library.Reviews.Domain.Entities;

/// <summary>
/// Коментар до відгуку — <b>вбудований піддокумент</b> (масив <c>comments</c>
/// у схемі Лаб. 01), а не окрема сутність.
/// </summary>
/// <remarks>
/// Вбудовується, бо коментарі завжди читаються разом із відгуком і їх небагато.
/// Довге обговорення натомість винесено в окрему сутність
/// <see cref="Discussion"/> і підключається <b>за посиланням</b> — класичний
/// гібридний підхід MongoDB.
/// </remarks>
public sealed class Comment
{
    public Author Author { get; private set; } = null!;
    public string Text { get; private set; } = "";
    public DateTime CreatedAt { get; private set; }
    public int Likes { get; private set; }

    private Comment() { }                       // для десеріалізації BSON

    public Comment(Author author, string text, DateTime createdAt)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (string.IsNullOrWhiteSpace(text))
            throw new DomainException("Comment text is required");
        if (text.Length > 1000)
            throw new DomainException("Comment text must not exceed 1000 characters");

        Author = author;
        Text = text.Trim();
        CreatedAt = createdAt;
    }

    public void Like() => Likes++;
}

/// <summary>
/// Відгук на книгу — коренева сутність агрегату контексту «Відгуки».
/// </summary>
/// <remarks>
/// <para>
/// Повторює структуру документа <c>reviews</c> з Лаб. 01 і демонструє всі
/// прийоми моделювання MongoDB одночасно:
/// <list type="bullet">
///   <item><b>вбудований об'єкт</b> — <see cref="Author"/> (Value Object);</item>
///   <item><b>вбудований масив</b> — <see cref="Comments"/>;</item>
///   <item><b>посилання</b> — <see cref="DiscussionId"/> на окрему колекцію;</item>
///   <item><b>денормалізація</b> — <see cref="Book"/> зберігає назву книги;</item>
///   <item><b>гнучка схема</b> — <see cref="Photos"/> і <see cref="Tags"/>
///         є не в усіх документів.</item>
/// </list>
/// </para>
/// <para>
/// Інкапсуляція: усі властивості мають <c>private set</c>, колекції віддаються
/// тільки для читання, стан змінюють лише методи — і вони ж боронять інваріанти.
/// </para>
/// </remarks>
public sealed class Review : BaseEntity
{
    // Поля-колекції НЕ readonly: драйвер MongoDB присвоює їх під час
    // десеріалізації. Інкапсуляцію це не послаблює — поля приватні, а назовні
    // колекції віддаються лише для читання (IReadOnlyCollection).
    private List<Comment> _comments = [];
    private List<string> _photos = [];
    private List<string> _tags = [];

    // Компоненти BookRef зберігаються ОКРЕМИМИ полями, бо в документі з Лаб. 01
    // bookId і bookTitle лежать на верхньому рівні, а не всередині вкладеного
    // об'єкта. Домен при цьому працює з повноцінним Value Object (властивість
    // Book нижче) — деталь зберігання не протікає в доменну модель.
    private long _bookId;
    private string _bookTitle = "";

    /// <summary>Посилання на книгу + денормалізована назва (політика дублювання, Лаб. 01).</summary>
    public BookRef Book => new(_bookId, _bookTitle);

    /// <summary>Автор відгуку — вбудований Value Object.</summary>
    public Author Author { get; private set; } = null!;

    public Rating Rating { get; private set; } = null!;

    public string? Text { get; private set; }

    /// <summary>Посилання на довге обговорення в окремій колекції (може бути відсутнє).</summary>
    public ObjectId? DiscussionId { get; private set; }

    public IReadOnlyCollection<Comment> Comments => _comments.AsReadOnly();
    public IReadOnlyCollection<string> Photos => _photos.AsReadOnly();
    public IReadOnlyCollection<string> Tags => _tags.AsReadOnly();

    private Review() { }                        // для десеріалізації BSON

    public Review(BookRef book, Author author, int rating, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(author);

        if (text is { Length: > 2000 })
            throw new DomainException("Review text must not exceed 2000 characters");

        _bookId = book.BookId;
        _bookTitle = book.BookTitle;
        Author = author;
        Rating = new Rating(rating);            // Value Object сам перевіряє 1..5
        Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Змінює оцінку й текст відгуку.</summary>
    public void Update(int rating, string? text)
    {
        if (text is { Length: > 2000 })
            throw new DomainException("Review text must not exceed 2000 characters");

        Rating = new Rating(rating);
        Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        Touch();
    }

    /// <summary>Додає вбудований коментар.</summary>
    public void AddComment(Author author, string text)
    {
        if (_comments.Count >= 50)
            throw new DomainException(
                "A review cannot hold more than 50 embedded comments; use a discussion instead");

        _comments.Add(new Comment(author, text, DateTime.UtcNow));
        Touch();
    }

    /// <summary>Прикріплює посилання на обговорення (гібридний підхід).</summary>
    public void AttachDiscussion(ObjectId discussionId)
    {
        if (discussionId == ObjectId.Empty)
            throw new DomainException("DiscussionId must not be empty");

        DiscussionId = discussionId;
        Touch();
    }

    /// <summary>Додає фото — поле, якого немає в більшості документів (гнучка схема).</summary>
    public void AddPhoto(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new DomainException("Photo url is required");
        if (_photos.Contains(url))
            throw new DomainException($"Photo '{url}' is already attached");

        _photos.Add(url.Trim());
        Touch();
    }

    public void AddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new DomainException("Tag is required");

        var normalized = tag.Trim().ToLowerInvariant();
        if (_tags.Contains(normalized))
            throw new DomainException($"Tag '{normalized}' is already present");

        _tags.Add(normalized);
        Touch();
    }

    /// <summary>Оновлює денормалізовану назву книги — реакція на подію з Каталогу.</summary>
    public void RefreshBookTitle(string newTitle)
    {
        _bookTitle = new BookRef(_bookId, newTitle).BookTitle;   // VO валідує назву
        Touch();
    }
}
