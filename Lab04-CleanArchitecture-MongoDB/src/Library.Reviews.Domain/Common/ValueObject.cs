namespace Library.Reviews.Domain.Common;

/// <summary>
/// Базовий клас Value Object: об'єкти порівнюються <b>за значенням</b>
/// компонентів, а не за посиланням.
/// </summary>
/// <remarks>
/// Побудовано за зразком <c>ValueObject</c> з референсного застосунку
/// Microsoft eShop (<c>Ordering.Domain/SeedWork/ValueObject.cs</c>).
/// Нащадки мають бути <b>незмінними</b>: без setter-ів, уся валідація —
/// в конструкторі.
/// </remarks>
public abstract class ValueObject
{
    /// <summary>Компоненти, за якими визначається рівність.</summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public override bool Equals(object? obj)
        => obj is ValueObject other
           && GetType() == other.GetType()
           && GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());

    public override int GetHashCode()
        => GetEqualityComponents()
            .Aggregate(0, (hash, part) => HashCode.Combine(hash, part));

    public static bool operator ==(ValueObject? left, ValueObject? right)
        => Equals(left, right);

    public static bool operator !=(ValueObject? left, ValueObject? right)
        => !Equals(left, right);
}
