using FluentValidation;
using Library.Catalog.Bll.Dtos;
using Library.Catalog.Dal.Specifications;

namespace Library.Catalog.Bll.Validators;

/* =============================================================================
   FluentValidation — ДРУГИЙ рівень валідації (Частина 4.3 Лаб. 03).

   Розподіл обов'язків, щоб не дублювати правила:
     DataAnnotations (на DTO) — ФОРМАТ: обов'язковість, довжина, діапазон.
     FluentValidation (тут)   — СКЛАДНІ правила: регулярні вирази, умовні
                                перевірки, взаємозалежні поля, контрольна сума.

   ВАЖЛИВО: автоматична валідація через пакет FluentValidation.AspNetCore
   (AddFluentValidation) ЗАСТАРІЛА і більше не підтримується. Валідатори
   реєструються через FluentValidation.DependencyInjectionExtensions
   (AddValidatorsFromAssemblyContaining) і викликаються ЯВНО в контролері.
   ============================================================================= */

public sealed class CreateBookDtoValidator : AbstractValidator<CreateBookDto>
{
    public CreateBookDtoValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Назва книги обовʼязкова")
            .MaximumLength(300);

        RuleFor(x => x.Isbn)
            .NotEmpty().WithMessage("ISBN обовʼязковий")
            .Matches(@"^[0-9]{3}-[0-9]{3}-[0-9]{2}-[0-9]{4}-[0-9]$")
                .WithMessage("ISBN має бути у форматі 978-966-03-5555-1");

        RuleFor(x => x.DailyFee)
            .GreaterThan(0).WithMessage("Денний тариф має бути більшим за нуль")
            .LessThanOrEqualTo(100_000);

        RuleFor(x => x.AuthorId)
            .GreaterThan(0).WithMessage("Потрібно вказати автора");

        RuleFor(x => x.PublishedYear)
            .InclusiveBetween(1400, 2100)
            .When(x => x.PublishedYear.HasValue)
            .WithMessage("Рік видання має бути в межах 1400–2100");

        // взаємозалежні поля: якщо вказано видавця або кількість сторінок,
        // опис стає обовʼязковим — інакше вийде «напівпорожній» BookDetail
        RuleFor(x => x.Description)
            .NotEmpty()
            .When(x => !string.IsNullOrWhiteSpace(x.Publisher) || x.PageCount.HasValue)
            .WithMessage("Якщо вказано видавця або кількість сторінок, опис обовʼязковий");

        RuleFor(x => x.GenreIds)
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("Жанри не повинні повторюватися");

        RuleForEach(x => x.GenreIds)
            .GreaterThan(0).WithMessage("Ідентифікатор жанру має бути додатним");
    }
}

public sealed class UpdateBookDtoValidator : AbstractValidator<UpdateBookDto>
{
    public UpdateBookDtoValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.DailyFee).GreaterThan(0).LessThanOrEqualTo(100_000);
    }
}

public sealed class CreateAuthorDtoValidator : AbstractValidator<CreateAuthorDto>
{
    public CreateAuthorDtoValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Імʼя автора обовʼязкове")
            .MinimumLength(2)
            .MaximumLength(200);

        RuleFor(x => x.BirthYear)
            .InclusiveBetween(1000, 2100)
            .When(x => x.BirthYear.HasValue);
    }
}

public sealed class AddCopyDtoValidator : AbstractValidator<AddCopyDto>
{
    public AddCopyDtoValidator()
    {
        RuleFor(x => x.InventoryNumber)
            .NotEmpty()
            .Matches("^[A-Z0-9-]+$")
                .WithMessage("Інвентарний номер може містити лише великі латинські літери, цифри й дефіс");

        RuleFor(x => x.Condition)
            .Must(Domain.Entities.CopyCondition.IsValid)
            .WithMessage($"Стан має бути одним із: {string.Join(", ", Domain.Entities.CopyCondition.All)}");

        RuleFor(x => x.AcquiredAt)
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .When(x => x.AcquiredAt.HasValue)
            .WithMessage("Дата надходження не може бути в майбутньому");
    }
}

/// <summary>
/// Валідація параметрів списку: захищає від довільних полів сортування
/// та завеликих сторінок.
/// </summary>
public sealed class BookQueryValidator : AbstractValidator<BookQuery>
{
    public BookQueryValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThan(0).WithMessage("Номер сторінки починається з 1");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, BookQuery.MaxPageSize)
            .WithMessage($"Розмір сторінки має бути в межах 1–{BookQuery.MaxPageSize}");

        RuleFor(x => x.SortBy)
            .Must(f => string.IsNullOrWhiteSpace(f) ||
                       BookQuery.AllowedSortFields.Contains(f.ToLowerInvariant()))
            .WithMessage($"Сортувати можна лише за: {string.Join(", ", BookQuery.AllowedSortFields)}");

        RuleFor(x => x.SortDir)
            .Must(d => string.IsNullOrWhiteSpace(d) ||
                       d.Equals("asc", StringComparison.OrdinalIgnoreCase) ||
                       d.Equals("desc", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Напрямок сортування: asc або desc");

        RuleFor(x => x.MaxFee)
            .GreaterThanOrEqualTo(x => x.MinFee!.Value)
            .When(x => x.MinFee.HasValue && x.MaxFee.HasValue)
            .WithMessage("maxFee не може бути меншим за minFee");
    }
}
