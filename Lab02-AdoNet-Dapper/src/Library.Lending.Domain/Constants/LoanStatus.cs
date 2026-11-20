namespace Library.Lending.Domain.Constants;

/// <summary>
/// Статуси видачі книг та допустимі переходи між ними.
/// Повторює CHECK-обмеження <c>CK_Loans_Status</c> з Лаб. 01 і побудовано
/// за зразком <c>OrderStatus</c> з референсного застосунку Microsoft eShop.
/// </summary>
public static class LoanStatus
{
    public const string Submitted          = "Submitted";
    public const string AwaitingValidation = "AwaitingValidation";
    public const string CopiesConfirmed    = "CopiesConfirmed";
    public const string Issued             = "Issued";
    public const string Returned           = "Returned";
    public const string Cancelled          = "Cancelled";

    public static readonly IReadOnlyCollection<string> All = new[]
    {
        Submitted, AwaitingValidation, CopiesConfirmed, Issued, Returned, Cancelled
    };

    /// <summary>
    /// Допустимі переходи. Остаточне рішення ухвалює збережувана процедура в БД
    /// (єдине джерело істини); ця карта потрібна для швидкої перевірки в BLL
    /// та для документування контракту API.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedTransitions =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Submitted]          = [AwaitingValidation, CopiesConfirmed, Cancelled],
            [AwaitingValidation] = [CopiesConfirmed, Cancelled],
            [CopiesConfirmed]    = [Issued, Cancelled],
            [Issued]             = [Returned],
            [Returned]           = [],
            [Cancelled]          = []
        };

    public static bool IsKnown(string? status) =>
        status is not null && All.Contains(status);

    public static bool CanTransition(string from, string to) =>
        AllowedTransitions.TryGetValue(from, out var next) && next.Contains(to);
}
