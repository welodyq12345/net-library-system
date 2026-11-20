namespace Library.Reviews.Domain.Exceptions;

/// <summary>
/// Порушення доменного правила. Кидається САМИМИ сутностями та Value Objects.
/// Обробник в WebApi перетворює його на <b>400 Bad Request</b>.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>Документ відсутній. Обробник повертає <b>404 Not Found</b>.</summary>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string message) : base(message) { }

    public static NotFoundException For(string entity, object id) =>
        new($"{entity} with id {id} was not found");
}

/// <summary>
/// Конфлікт стану: інший користувач змінив документ раніше (конфлікт версій)
/// або порушено унікальність. Обробник повертає <b>409 Conflict</b>.
/// </summary>
public sealed class ConflictException : DomainException
{
    public ConflictException(string message) : base(message) { }
}
