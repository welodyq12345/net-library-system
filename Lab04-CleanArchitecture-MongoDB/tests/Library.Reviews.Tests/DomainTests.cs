using Library.Reviews.Domain.Common;
using Library.Reviews.Domain.Entities;
using Library.Reviews.Domain.Exceptions;
using Library.Reviews.Domain.ValueObjects;
using MongoDB.Bson;

namespace Library.Reviews.Tests;

/// <summary>
/// Тести доменного шару. Жоден із них не звертається до бази даних — це
/// пряма перевага Clean Architecture: доменні правила перевіряються
/// без MongoDB, без моків і без HTTP.
/// </summary>
public class ReviewEntityTests
{
    private static BookRef Book => new(1, "Кобзар");
    private static Author Reader => new(1, "Олена К.");

    /// <summary>Контрольна точка методички (Частина 2.3).</summary>
    [Fact]
    public void Constructor_WithRating10_ThrowsDomainException_WithoutDatabase()
    {
        var ex = Assert.Throws<DomainException>(() => new Review(Book, Reader, 10, "текст"));

        Assert.Contains("Rating must be between 1 and 5", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public void Constructor_RejectsRatingOutOfRange(int rating)
        => Assert.Throws<DomainException>(() => new Review(Book, Reader, rating, "текст"));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Constructor_AcceptsValidRating(int rating)
    {
        var review = new Review(Book, Reader, rating, "текст");
        Assert.Equal(rating, review.Rating.Value);
    }

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var review = new Review(Book, Reader, 5, "  текст  ");

        Assert.NotEqual(ObjectId.Empty, review.Id);
        Assert.Equal("текст", review.Text);
        Assert.Equal(0, review.Version);
        Assert.Null(review.UpdatedAt);
        Assert.Empty(review.Comments);
        Assert.Equal(1, review.Book.BookId);
    }

    [Fact]
    public void Constructor_AllowsNullText()
    {
        var review = new Review(Book, Reader, 4);
        Assert.Null(review.Text);
    }

    [Fact]
    public void Constructor_RejectsTooLongText()
        => Assert.Throws<DomainException>(() => new Review(Book, Reader, 4, new string('x', 2001)));

    [Fact]
    public void AddComment_AppendsEmbeddedComment_AndTouches()
    {
        var review = new Review(Book, Reader, 5, "текст");

        review.AddComment(new Author(2, "Андрій Ш."), "Згоден");

        var comment = Assert.Single(review.Comments);
        Assert.Equal("Андрій Ш.", comment.Author.DisplayName);
        Assert.Equal("Згоден", comment.Text);
        Assert.NotNull(review.UpdatedAt);
    }

    [Fact]
    public void AddComment_RejectsEmptyText()
    {
        var review = new Review(Book, Reader, 5, "текст");
        Assert.Throws<DomainException>(() => review.AddComment(Reader, "   "));
        Assert.Empty(review.Comments);
    }

    /// <summary>
    /// Інваріант, що пояснює гібридну модель: довгі ветки не вбудовуються
    /// у відгук, а виносяться в окрему колекцію.
    /// </summary>
    [Fact]
    public void AddComment_RejectsMoreThan50Comments()
    {
        var review = new Review(Book, Reader, 5, "текст");
        for (var i = 0; i < 50; i++)
            review.AddComment(Reader, $"коментар {i}");

        var ex = Assert.Throws<DomainException>(() => review.AddComment(Reader, "зайвий"));

        Assert.Contains("discussion", ex.Message);
        Assert.Equal(50, review.Comments.Count);
    }

    [Fact]
    public void AttachDiscussion_SetsReference()
    {
        var review = new Review(Book, Reader, 5, "текст");
        var discussionId = ObjectId.GenerateNewId();

        review.AttachDiscussion(discussionId);

        Assert.Equal(discussionId, review.DiscussionId);
    }

    [Fact]
    public void AttachDiscussion_RejectsEmptyId()
    {
        var review = new Review(Book, Reader, 5, "текст");
        Assert.Throws<DomainException>(() => review.AttachDiscussion(ObjectId.Empty));
    }

    [Fact]
    public void AddPhoto_RejectsDuplicate()
    {
        var review = new Review(Book, Reader, 5, "текст");
        review.AddPhoto("cover.jpg");

        Assert.Throws<DomainException>(() => review.AddPhoto("cover.jpg"));
        Assert.Single(review.Photos);
    }

    [Fact]
    public void AddTag_NormalizesToLowerCase_AndRejectsDuplicate()
    {
        var review = new Review(Book, Reader, 5, "текст");
        review.AddTag("  Війна  ");

        Assert.Equal("війна", Assert.Single(review.Tags));
        Assert.Throws<DomainException>(() => review.AddTag("ВІЙНА"));
    }

    [Fact]
    public void RefreshBookTitle_UpdatesDenormalizedCopy()
    {
        var review = new Review(Book, Reader, 5, "текст");

        review.RefreshBookTitle("Кобзар (нове видання)");

        Assert.Equal("Кобзар (нове видання)", review.Book.BookTitle);
        Assert.Equal(1, review.Book.BookId);           // Id не змінюється
    }

    [Fact]
    public void BumpVersion_IncrementsVersion()
    {
        var review = new Review(Book, Reader, 5, "текст");

        review.BumpVersion();
        review.BumpVersion();

        Assert.Equal(2, review.Version);
    }

    [Fact]
    public void Comments_AreReadOnly_FromOutside()
    {
        var review = new Review(Book, Reader, 5, "текст");
        Assert.IsAssignableFrom<IReadOnlyCollection<Comment>>(review.Comments);
    }
}

public class DiscussionEntityTests
{
    private static BookRef Book => new(1, "Кобзар");

    [Fact]
    public void Post_AddsMessage_AndUpdatesActivity()
    {
        var discussion = new Discussion(Book, "Тема");

        discussion.Post(new Author(1, "Олена"), "Перше");

        Assert.Single(discussion.Messages);
        Assert.NotNull(discussion.LastActivityAt);
    }

    [Fact]
    public void Post_IntoClosedDiscussion_Throws()
    {
        var discussion = new Discussion(Book, "Тема");
        discussion.Post(new Author(1, "Олена"), "Перше");
        discussion.Close("досить");

        var ex = Assert.Throws<DomainException>(
            () => discussion.Post(new Author(1, "Олена"), "Ще"));

        Assert.Contains("closed", ex.Message);
        Assert.Single(discussion.Messages);
    }

    [Fact]
    public void Post_WithInvalidReplyTo_Throws()
    {
        var discussion = new Discussion(Book, "Тема");

        Assert.Throws<DomainException>(
            () => discussion.Post(new Author(1, "Олена"), "Відповідь", replyTo: 5));
    }

    [Fact]
    public void Close_Twice_Throws()
    {
        var discussion = new Discussion(Book, "Тема");
        discussion.Close();

        Assert.Throws<DomainException>(() => discussion.Close());
    }

    [Fact]
    public void Reopen_AllowsPostingAgain()
    {
        var discussion = new Discussion(Book, "Тема");
        discussion.Close();
        discussion.Reopen();

        discussion.Post(new Author(1, "Олена"), "Знову відкрито");

        Assert.Single(discussion.Messages);
        Assert.False(discussion.IsClosed);
    }

    [Fact]
    public void Constructor_RejectsEmptyTopic()
        => Assert.Throws<DomainException>(() => new Discussion(Book, "   "));
}

/// <summary>Тести Value Objects — порівняння за значенням і незмінність.</summary>
public class ValueObjectTests
{
    [Fact]
    public void Author_EqualByValue_NotByReference()
    {
        var a = new Author(1, "Олена К.");
        var b = new Author(1, "Олена К.");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(ReferenceEquals(a, b));
    }

    [Fact]
    public void Author_DifferentComponents_AreNotEqual()
    {
        Assert.NotEqual(new Author(1, "Олена"), new Author(2, "Олена"));
        Assert.NotEqual(new Author(1, "Олена"), new Author(1, "Андрій"));
        Assert.NotEqual(new Author(null, "Олена"), new Author(1, "Олена"));
    }

    [Fact]
    public void Author_Anonymous_HasNoReaderId()
    {
        var anonymous = Author.Anonymous();

        Assert.Null(anonymous.ReaderId);
        Assert.True(anonymous.IsAnonymous);
    }

    [Fact]
    public void Author_RejectsEmptyDisplayName()
        => Assert.Throws<DomainException>(() => new Author(1, "   "));

    [Fact]
    public void Author_RejectsNonPositiveReaderId()
        => Assert.Throws<DomainException>(() => new Author(0, "Олена"));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Rating_RejectsOutOfRange(int value)
        => Assert.Throws<DomainException>(() => new Rating(value));

    [Fact]
    public void Rating_EqualByValue()
    {
        Assert.Equal(new Rating(5), new Rating(5));
        Assert.NotEqual(new Rating(5), new Rating(4));
    }

    [Fact]
    public void Rating_ImplicitlyConvertsToInt()
    {
        int value = new Rating(4);
        Assert.Equal(4, value);
    }

    [Fact]
    public void BookRef_EqualByValue()
    {
        Assert.Equal(new BookRef(1, "Кобзар"), new BookRef(1, "Кобзар"));
        Assert.NotEqual(new BookRef(1, "Кобзар"), new BookRef(1, "Інша назва"));
    }

    [Fact]
    public void BookRef_RejectsInvalidInput()
    {
        Assert.Throws<DomainException>(() => new BookRef(0, "Кобзар"));
        Assert.Throws<DomainException>(() => new BookRef(1, " "));
    }

    [Fact]
    public void ValueObject_OfDifferentTypes_AreNotEqual()
    {
        ValueObject author = new Author(1, "Олена");
        ValueObject book = new BookRef(1, "Олена");

        Assert.NotEqual(author, book);
    }
}
