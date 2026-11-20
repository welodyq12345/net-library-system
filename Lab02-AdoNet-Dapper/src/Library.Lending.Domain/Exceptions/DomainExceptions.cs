namespace Library.Lending.Domain.Exceptions;

/// <summary>
/// Базовий доменний виняток. Оголошений у Domain, щоб його бачили і DAL, і BLL.
/// «Сирі» повідомлення SQL Server ніколи не доходять до API — DAL перетворює
/// <c>SqlException</c> на ці типи.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>Ресурс відсутній. Обробник API повертає <b>404 Not Found</b>.</summary>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string message, Exception? inner = null)
        : base(message, inner) { }

    public static NotFoundException For(string entity, object id) =>
        new($"{entity} with id {id} was not found");
}

/// <summary>
/// Порушено бізнес-правило або недопустимий перехід статусу.
/// Обробник API повертає <b>409 Conflict</b>.
/// </summary>
public sealed class BusinessConflictException : DomainException
{
    public BusinessConflictException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Некоректні вхідні дані, виявлені в BLL (там, де потрібен стан із БД).
/// Обробник API повертає <b>400 Bad Request</b>.
/// Проста модельна валідація робиться раніше — атрибутами DataAnnotations на DTO.
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
