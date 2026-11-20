using Library.Reviews.Domain.Entities;
using MongoDB.Bson;

namespace Library.Reviews.Domain.Interfaces;

/// <summary>
/// Репозиторій відгуків. Інтерфейс оголошено в <b>Domain</b>, реалізацію
/// написано в <b>Infrastructure</b> — це і є інверсія залежностей
/// Clean Architecture.
/// </summary>
/// <remarks>
/// Методи оперують доменними сутностями; жоден не повертає
/// <c>IMongoCollection</c>, <c>BsonDocument</c> чи інші типи драйвера.
/// </remarks>
public interface IReviewRepository
{
    Task<Review?> GetByIdAsync(ObjectId id, CancellationToken ct = default);

    /// <summary>Чи залишав цей читач відгук на цю книгу (бізнес-правило «один відгук»).</summary>
    Task<bool> ExistsAsync(long bookId, long? readerId, CancellationToken ct = default);

    Task AddAsync(Review review, CancellationToken ct = default);

    /// <summary>
    /// Оновлення з перевіркою версії. Якщо документ змінили раніше —
    /// кидає <c>ConflictException</c> (оптимістична конкурентність).
    /// </summary>
    Task UpdateAsync(Review review, CancellationToken ct = default);

    Task<bool> DeleteAsync(ObjectId id, CancellationToken ct = default);

    /// <summary>Оновлює денормалізовану назву книги в усіх відгуках (реакція на подію Каталогу).</summary>
    Task<long> RefreshBookTitleAsync(long bookId, string newTitle, CancellationToken ct = default);
}

/// <summary>Репозиторій обговорень.</summary>
public interface IDiscussionRepository
{
    Task<Discussion?> GetByIdAsync(ObjectId id, CancellationToken ct = default);

    Task AddAsync(Discussion discussion, CancellationToken ct = default);

    Task UpdateAsync(Discussion discussion, CancellationToken ct = default);
}
