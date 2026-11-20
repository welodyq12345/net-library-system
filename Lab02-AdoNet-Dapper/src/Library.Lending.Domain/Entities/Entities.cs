using Library.Lending.Domain.Constants;

namespace Library.Lending.Domain.Entities;

/// <summary>
/// Читач бібліотеки. Власна сутність контексту «Видача».
/// Відповідає таблиці <c>dbo.Readers</c> з Лаб. 01.
/// </summary>
public class Reader
{
    public long Id { get; set; }
    public string CardNumber { get; set; } = "";
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public bool IsBlocked { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Локальна копія довідника книг із БД Каталогу (Проєкт №2).
/// <see cref="Id"/> — ТОЙ САМИЙ ідентифікатор, що й <c>Books.Id</c> у
/// <c>LibraryCatalogDb</c>, але FOREIGN KEY між базами не існує.
/// Оновлюється подіями з Каталогу (таблиця політики дублювання, Лаб. 01).
/// </summary>
public class Book
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string? Isbn { get; set; }
    public decimal DailyFee { get; set; }

    /// <summary>
    /// Залишок примірників, доступних для видачі. ВЛАСНІ дані цього контексту:
    /// змінюються операціями видачі/повернення, а не подіями з Каталогу.
    /// </summary>
    public int CopiesAvailable { get; set; }

    public DateTime SyncedAt { get; set; }
}

/// <summary>
/// Видача книг — коренева сутність агрегату (аналог <c>Order</c> в eShop).
/// </summary>
public class Loan
{
    public long Id { get; set; }
    public long ReaderId { get; set; }
    public string Status { get; set; } = LoanStatus.Submitted;
    public DateTime LoanDate { get; set; }
    public DateOnly DueDate { get; set; }
    public DateTime? IssuedAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public decimal FineAmount { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Позиції видачі. Частина агрегату: не існують без видачі.</summary>
    public List<LoanItem> Items { get; } = [];

    /// <summary>Деталі видачі (зв'язок 1:1).</summary>
    public LoanDetail? Detail { get; set; }

    public bool IsOverdue(DateOnly today) =>
        Status == LoanStatus.Issued && today > DueDate;
}

/// <summary>Деталі видачі. Зв'язок 1:1 — PK таблиці є водночас FK на <c>Loans</c>.</summary>
public class LoanDetail
{
    public long LoanId { get; set; }
    public string PickupBranch { get; set; } = "";
    public string? LibrarianName { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// Позиція видачі — проміжна сутність зв'язку M:N між <c>Loans</c> і <c>Books</c>.
/// <see cref="BookTitle"/>, <see cref="BookAuthor"/> та <see cref="DailyFee"/> —
/// ЗНІМОК полів каталогу на момент оформлення видачі: вони свідомо НЕ оновлюються
/// при зміні каталогу (так само, як <c>OrderItem</c> в eShop зберігає
/// <c>ProductName</c> і <c>UnitPrice</c>).
/// </summary>
public class LoanItem
{
    public long LoanId { get; set; }
    public long BookId { get; set; }
    public string BookTitle { get; set; } = "";
    public string BookAuthor { get; set; } = "";
    public decimal DailyFee { get; set; }
    public int Units { get; set; }
    public decimal Discount { get; set; }

    public decimal DailyCharge => DailyFee * Units - Discount;
}

/// <summary>Запис журналу переходів статусів видачі (1:N від <c>Loans</c>).</summary>
public class LoanStatusHistoryEntry
{
    public long Id { get; set; }
    public long LoanId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public string? Note { get; set; }
}

/// <summary>Оплата пені за прострочення (1:N від <c>Loans</c>).</summary>
public class FinePayment
{
    public long Id { get; set; }
    public long LoanId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "Cash";
    public DateTime PaidAt { get; set; }

    /// <summary>Токен ідемпотентності: повторна відправка не створює другий платіж.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// Рядок денормалізованого представлення <c>dbo.vw_LoanSummary</c> —
/// читальна модель для звітів (повертається процедурою
/// <c>usp_GetReaderLoanSummary</c>).
/// </summary>
public class LoanSummary
{
    public long LoanId { get; set; }
    public string Status { get; set; } = "";
    public DateTime LoanDate { get; set; }
    public DateOnly DueDate { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public decimal FineAmount { get; set; }
    public long ReaderId { get; set; }
    public string ReaderName { get; set; } = "";
    public string CardNumber { get; set; } = "";
    public string? PickupBranch { get; set; }
    public int TitlesCount { get; set; }
    public int UnitsTotal { get; set; }
    public decimal DailyChargeTotal { get; set; }
}
