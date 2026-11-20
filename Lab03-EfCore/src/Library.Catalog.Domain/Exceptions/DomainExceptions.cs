namespace Library.Catalog.Domain.Exceptions;

/// <summary>
/// Базовий доменний виняток. Кидається МЕТОДАМИ САМИХ СУТНОСТЕЙ, коли
/// порушено їхній внутрішній інваріант (ціна &gt; 0, достатній залишок).
/// Обробник в API перетворює його на <b>409 Conflict</b>.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>Сутність відсутня. Обробник API повертає <b>404 Not Found</b>.</summary>
public sealed class EntityNotFoundException : DomainException
{
    public EntityNotFoundException(string message) : base(message) { }

    public static EntityNotFoundException For(string entity, object id) =>
        new($"{entity} with id {id} was not found");
}

/// <summary>
/// Конфлікт стану: порушено правило, для перевірки якого потрібні інші дані з
/// БД (унікальність SKU/ISBN тощо). Обробник API повертає <b>409 Conflict</b>.
/// </summary>
public sealed class ConflictException : DomainException
{
    public ConflictException(string message) : base(message) { }
}

/// <summary>
/// Некоректні вхідні дані. Обробник API повертає <b>400 Bad Request</b>
/// у форматі <c>ValidationProblemDetails</c>.
/// </summary>
public sealed class ValidationException : DomainException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(string message)
        : base(message) => Errors = new Dictionary<string, string[]>();

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred") => Errors = errors;

    public ValidationException(string field, string error)
        : base("One or more validation errors occurred") =>
        Errors = new Dictionary<string, string[]> { [field] = [error] };
}
