namespace Library.Lending.Domain.Constants;

/// <summary>
/// Номери помилок, якими кидаються збережувані процедури з Лаб. 01
/// (<c>THROW 5000x, '...', 1</c>), та системні коди SQL Server.
/// DAL перехоплює <c>SqlException</c> за цими номерами й перетворює їх
/// на доменні винятки; обробник в API робить із них HTTP-статуси.
/// </summary>
public static class SqlErrorCodes
{
    /// <summary>Сутність не знайдено → <c>NotFoundException</c> → HTTP 404.</summary>
    public const int NotFound = 50001;

    /// <summary>Недопустимий перехід статусу → <c>BusinessConflictException</c> → HTTP 409.</summary>
    public const int InvalidTransition = 50002;

    /// <summary>Недостатньо примірників → <c>BusinessConflictException</c> → HTTP 409.</summary>
    public const int NotEnoughCopies = 50003;

    /// <summary>Порушення бізнес-правила → <c>BusinessConflictException</c> → HTTP 409.</summary>
    public const int BusinessRule = 50004;

    /// <summary>Порушення UNIQUE-індексу (SQL Server).</summary>
    public const int UniqueIndexViolation = 2601;

    /// <summary>Порушення UNIQUE/PK-обмеження (SQL Server).</summary>
    public const int UniqueConstraintViolation = 2627;

    /// <summary>Порушення FOREIGN KEY (SQL Server).</summary>
    public const int ForeignKeyViolation = 547;

    /// <summary>Порушення CHECK-обмеження (SQL Server) — той самий номер, що й FK.</summary>
    public const int CheckConstraintViolation = 547;
}
