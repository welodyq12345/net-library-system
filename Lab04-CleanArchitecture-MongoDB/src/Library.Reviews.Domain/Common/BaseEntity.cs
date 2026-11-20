using MongoDB.Bson;

namespace Library.Reviews.Domain.Common;

/// <summary>
/// Базовий клас доменних сутностей, адаптований під MongoDB.
/// </summary>
/// <remarks>
/// <para>
/// Domain-шар залежить лише від <c>MongoDB.Bson</c> — це пакет ТИПІВ
/// (<see cref="ObjectId"/>), а не драйвер доступу до бази. Жодного
/// <c>MongoDB.Driver</c>, жодного <c>IMongoCollection</c> тут немає і бути
/// не може: правило залежностей Clean Architecture забороняє внутрішньому
/// шару знати про зовнішні.
/// </para>
/// <para>
/// <see cref="Version"/> забезпечує <b>оптимістичну конкурентність</b>:
/// репозиторій в Infrastructure оновлює документ лише тоді, коли в базі
/// лежить саме очікувана версія (див. <c>ReviewRepository.UpdateAsync</c>).
/// </para>
/// </remarks>
public abstract class BaseEntity
{
    public ObjectId Id { get; protected set; } = ObjectId.GenerateNewId();

    public DateTime CreatedAt { get; protected set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; protected set; }

    /// <summary>Версія документа для оптимістичної конкурентності.</summary>
    public int Version { get; protected set; }

    /// <summary>Фіксує момент останньої зміни сутності.</summary>
    protected void Touch() => UpdatedAt = DateTime.UtcNow;

    /// <summary>Збільшує версію. Викликається репозиторієм перед записом.</summary>
    public void BumpVersion() => Version++;

    public override bool Equals(object? obj) =>
        obj is BaseEntity other && GetType() == other.GetType() && Id == other.Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
