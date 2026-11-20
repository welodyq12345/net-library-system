using Library.Reviews.Domain.Entities;
using Library.Reviews.Domain.Exceptions;
using Library.Reviews.Domain.Interfaces;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Library.Reviews.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IReviewRepository"/> на MongoDB Driver.
/// </summary>
/// <remarks>
/// Це єдине місце в усьому рішенні, де згадується
/// <c>IMongoCollection</c> для відгуків: ні контролери, ні обробники
/// MediatR до драйвера не звертаються.
/// </remarks>
public sealed class ReviewRepository(MongoDbContext context) : IReviewRepository
{
    private readonly IMongoCollection<Review> _reviews = context.Reviews;

    public async Task<Review?> GetByIdAsync(ObjectId id, CancellationToken ct = default)
        => await _reviews.Find(r => r.Id == id).FirstOrDefaultAsync(ct);

    public Task<bool> ExistsAsync(long bookId, long? readerId, CancellationToken ct = default)
    {
        if (readerId is null)
            return Task.FromResult(false);          // анонімні відгуки не обмежуємо

        return _reviews
            .Find(Builders<Review>.Filter.Eq("bookId", bookId)
                   & Builders<Review>.Filter.Eq("author.readerId", readerId))
            .AnyAsync(ct);
    }

    public Task AddAsync(Review review, CancellationToken ct = default)
        => _reviews.InsertOneAsync(review, cancellationToken: ct);

    /// <summary>
    /// Оновлення з <b>оптимістичною конкурентністю</b>: документ замінюється
    /// лише тоді, коли в базі лежить саме очікувана версія.
    /// </summary>
    /// <remarks>
    /// Якщо інший користувач змінив документ раніше, фільтр не знайде нічого
    /// (<c>MatchedCount == 0</c>) — і ми кидаємо
    /// <see cref="ConflictException"/>, що стане HTTP 409.
    /// </remarks>
    public async Task UpdateAsync(Review review, CancellationToken ct = default)
    {
        var expectedVersion = review.Version;
        review.BumpVersion();

        var result = await _reviews.ReplaceOneAsync(
            r => r.Id == review.Id && r.Version == expectedVersion,
            review,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
            throw new ConflictException(
                $"Review {review.Id} was modified by another user (expected version {expectedVersion})");
    }

    public async Task<bool> DeleteAsync(ObjectId id, CancellationToken ct = default)
    {
        var result = await _reviews.DeleteOneAsync(r => r.Id == id, ct);
        return result.DeletedCount > 0;
    }

    /// <summary>
    /// Масове оновлення денормалізованої назви книги — реакція на подію
    /// з сервісу Каталогу (політика дублювання з Лаб. 01).
    /// </summary>
    public async Task<long> RefreshBookTitleAsync(
        long bookId, string newTitle, CancellationToken ct = default)
    {
        var update = Builders<Review>.Update
            .Set("bookTitle", newTitle)
            .Set(r => r.UpdatedAt, DateTime.UtcNow)
            .Inc(r => r.Version, 1);

        var result = await _reviews.UpdateManyAsync(
            Builders<Review>.Filter.Eq("bookId", bookId), update, cancellationToken: ct);

        return result.ModifiedCount;
    }
}

/// <summary>Реалізація <see cref="IDiscussionRepository"/> на MongoDB Driver.</summary>
public sealed class DiscussionRepository(MongoDbContext context) : IDiscussionRepository
{
    private readonly IMongoCollection<Discussion> _discussions = context.Discussions;

    public async Task<Discussion?> GetByIdAsync(ObjectId id, CancellationToken ct = default)
        => await _discussions.Find(d => d.Id == id).FirstOrDefaultAsync(ct);

    public Task AddAsync(Discussion discussion, CancellationToken ct = default)
        => _discussions.InsertOneAsync(discussion, cancellationToken: ct);

    public async Task UpdateAsync(Discussion discussion, CancellationToken ct = default)
    {
        var expectedVersion = discussion.Version;
        discussion.BumpVersion();

        var result = await _discussions.ReplaceOneAsync(
            d => d.Id == discussion.Id && d.Version == expectedVersion,
            discussion,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
            throw new ConflictException(
                $"Discussion {discussion.Id} was modified by another user");
    }
}
