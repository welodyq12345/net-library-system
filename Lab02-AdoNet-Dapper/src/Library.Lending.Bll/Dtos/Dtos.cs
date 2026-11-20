using System.ComponentModel.DataAnnotations;

namespace Library.Lending.Bll.Dtos;

/* =============================================================================
   DTO — ЛИШЕ дані, без логіки. Атрибути DataAnnotations дають перший рівень
   валідації: некоректний запит відхиляється ще ДО виклику сервісу
   (ApiController автоматично повертає 400 ValidationProblemDetails).
   Другий рівень — бізнес-валідація в BLL, де потрібен стан із БД.
   ============================================================================= */

// ----------------------------------------------------------------- Readers ---

public record ReaderDto(
    long Id,
    string CardNumber,
    string Email,
    string FullName,
    string? Phone,
    bool IsBlocked,
    DateTime CreatedAt);

public class CreateReaderDto
{
    [Required, StringLength(20, MinimumLength = 3)]
    public string CardNumber { get; set; } = "";

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 2)]
    public string FullName { get; set; } = "";

    [Phone, StringLength(30)]
    public string? Phone { get; set; }
}

// ------------------------------------------------------------------- Books ---

public record BookDto(
    long Id,
    string Title,
    string Author,
    string? Isbn,
    decimal DailyFee,
    int CopiesAvailable,
    DateTime SyncedAt);

/// <summary>Синхронізація локальної копії довідника з БД Каталогу (Проєкт №2).</summary>
public class SyncBookDto
{
    [Range(1, long.MaxValue)]
    public long Id { get; set; }

    [Required, StringLength(300)]
    public string Title { get; set; } = "";

    [Required, StringLength(200)]
    public string Author { get; set; } = "";

    [StringLength(20)]
    public string? Isbn { get; set; }

    [Range(0.01, 100_000)]
    public decimal DailyFee { get; set; }

    [Range(0, int.MaxValue)]
    public int CopiesAvailable { get; set; }
}

// ------------------------------------------------------------------- Loans ---

public record LoanItemDto(
    long BookId,
    string BookTitle,
    string BookAuthor,
    decimal DailyFee,
    int Units,
    decimal Discount,
    decimal DailyCharge);

public record LoanDetailDto(
    string PickupBranch,
    string? LibrarianName,
    string? Comment);

public record LoanDto(
    long Id,
    long ReaderId,
    string Status,
    DateTime LoanDate,
    DateOnly DueDate,
    DateTime? IssuedAt,
    DateTime? ReturnedAt,
    decimal FineAmount,
    LoanDetailDto? Detail,
    IReadOnlyList<LoanItemDto> Items);

public class CreateLoanItemDto
{
    [Range(1, long.MaxValue)]
    public long BookId { get; set; }

    [Range(1, 100)]
    public int Units { get; set; } = 1;
}

public class CreateLoanDto
{
    [Range(1, long.MaxValue)]
    public long ReaderId { get; set; }

    [Required]
    public DateOnly DueDate { get; set; }

    [Required, StringLength(100, MinimumLength = 2)]
    public string PickupBranch { get; set; } = "";

    [StringLength(1000)]
    public string? Comment { get; set; }

    [Required, MinLength(1, ErrorMessage = "A loan must contain at least one item")]
    public List<CreateLoanItemDto> Items { get; set; } = [];
}

public class ReturnLoanDto
{
    /// <summary>Дата повернення. Якщо не вказана — поточний момент UTC.</summary>
    public DateTime? ReturnDate { get; set; }
}

public class CancelLoanDto
{
    [StringLength(400)]
    public string? Reason { get; set; }
}

public record LoanStatusHistoryDto(
    long Id,
    string? FromStatus,
    string ToStatus,
    DateTime ChangedAt,
    string? ChangedBy,
    string? Note);

public record LoanSummaryDto(
    long LoanId,
    string Status,
    DateTime LoanDate,
    DateOnly DueDate,
    DateTime? ReturnedAt,
    decimal FineAmount,
    long ReaderId,
    string ReaderName,
    string CardNumber,
    string? PickupBranch,
    int TitlesCount,
    int UnitsTotal,
    decimal DailyChargeTotal);

// ------------------------------------------------------------ FinePayments ---

public record FinePaymentDto(
    long Id,
    long LoanId,
    decimal Amount,
    string Method,
    DateTime PaidAt,
    string? IdempotencyKey);

public class PayFineDto
{
    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    [Required]
    [RegularExpression("^(Cash|Card|Online)$", ErrorMessage = "Method must be Cash, Card or Online")]
    public string Method { get; set; } = "Cash";

    /// <summary>
    /// Токен ідемпотентності: повторна відправка того самого запиту
    /// не створює другий платіж.
    /// </summary>
    [StringLength(64, MinimumLength = 8)]
    public string? IdempotencyKey { get; set; }
}
