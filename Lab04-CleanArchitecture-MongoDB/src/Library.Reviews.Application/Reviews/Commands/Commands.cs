using FluentValidation;
using Library.Reviews.Application.Common.Messaging;
using Library.Reviews.Domain.Entities;
using Library.Reviews.Domain.Exceptions;
using Library.Reviews.Domain.Interfaces;
using Library.Reviews.Domain.ValueObjects;
using MediatR;
using MongoDB.Bson;

namespace Library.Reviews.Application.Reviews.Commands;

/* =============================================================================
   КОМАНДИ (сторона запису CQRS).
   Кожна операція — окремий об'єкт-запит з окремим обробником і валідатором.
   Обробники працюють ЛИШЕ через доменні інтерфейси (IReviewRepository);
   жоден із них не бачить IMongoCollection.
   ============================================================================= */

// ------------------------------------------------------------ CreateReview ---

public record CreateReviewCommand(
    long BookId,
    string BookTitle,
    long? ReaderId,
    string AuthorName,
    int Rating,
    string? Text) : ICommand<string>;

public sealed class CreateReviewCommandHandler(IReviewRepository reviews)
    : IRequestHandler<CreateReviewCommand, string>
{
    public async Task<string> Handle(CreateReviewCommand cmd, CancellationToken ct)
    {
        var review = new Review(
            new BookRef(cmd.BookId, cmd.BookTitle),
            new Author(cmd.ReaderId, cmd.AuthorName),
            cmd.Rating,
            cmd.Text);

        await reviews.AddAsync(review, ct);
        return review.Id.ToString();
    }
}

public sealed class CreateReviewCommandValidator : AbstractValidator<CreateReviewCommand>
{
    public CreateReviewCommandValidator(IReviewRepository reviews)
    {
        RuleFor(x => x.BookId).GreaterThan(0);
        RuleFor(x => x.BookTitle).NotEmpty().MaximumLength(300);
        RuleFor(x => x.AuthorName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Rating).InclusiveBetween(Rating.Min, Rating.Max);
        RuleFor(x => x.Text).MaximumLength(2000);
        RuleFor(x => x.ReaderId).GreaterThan(0).When(x => x.ReaderId.HasValue);

        // АСИНХРОННА валідація із запитом до MongoDB: один читач — один відгук
        // на книгу. Для анонімних відгуків правило не застосовується.
        RuleFor(x => x)
            .MustAsync(async (cmd, ct) =>
                cmd.ReaderId is null || !await reviews.ExistsAsync(cmd.BookId, cmd.ReaderId, ct))
            .WithName("Review")
            .WithMessage("Цей читач уже залишив відгук на цю книгу");
    }
}

// ------------------------------------------------------------ UpdateReview ---

public record UpdateReviewCommand(string Id, int Rating, string? Text, int? ExpectedVersion)
    : ICommand;

public sealed class UpdateReviewCommandHandler(IReviewRepository reviews)
    : IRequestHandler<UpdateReviewCommand>
{
    public async Task Handle(UpdateReviewCommand cmd, CancellationToken ct)
    {
        var id = ObjectId.Parse(cmd.Id);
        var review = await reviews.GetByIdAsync(id, ct)
                     ?? throw NotFoundException.For("Review", cmd.Id);

        // перевірка If-Match: клієнт редагував саме цю версію документа
        if (cmd.ExpectedVersion is { } expected && review.Version != expected)
            throw new ConflictException(
                $"Review {cmd.Id} has version {review.Version}, but {expected} was expected");

        review.Update(cmd.Rating, cmd.Text);
        await reviews.UpdateAsync(review, ct);
    }
}

public sealed class UpdateReviewCommandValidator : AbstractValidator<UpdateReviewCommand>
{
    public UpdateReviewCommandValidator()
    {
        RuleFor(x => x.Id)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Id must be a valid ObjectId");
        RuleFor(x => x.Rating).InclusiveBetween(Rating.Min, Rating.Max);
        RuleFor(x => x.Text).MaximumLength(2000);
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0).When(x => x.ExpectedVersion.HasValue);
    }
}

// ------------------------------------------------------------ DeleteReview ---

public record DeleteReviewCommand(string Id) : ICommand;

public sealed class DeleteReviewCommandHandler(IReviewRepository reviews)
    : IRequestHandler<DeleteReviewCommand>
{
    public async Task Handle(DeleteReviewCommand cmd, CancellationToken ct)
    {
        if (!await reviews.DeleteAsync(ObjectId.Parse(cmd.Id), ct))
            throw NotFoundException.For("Review", cmd.Id);
    }
}

public sealed class DeleteReviewCommandValidator : AbstractValidator<DeleteReviewCommand>
{
    public DeleteReviewCommandValidator()
        => RuleFor(x => x.Id)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Id must be a valid ObjectId");
}

// -------------------------------------------------------------- AddComment ---

public record AddCommentCommand(string ReviewId, long? ReaderId, string AuthorName, string Text)
    : ICommand;

public sealed class AddCommentCommandHandler(IReviewRepository reviews)
    : IRequestHandler<AddCommentCommand>
{
    public async Task Handle(AddCommentCommand cmd, CancellationToken ct)
    {
        var id = ObjectId.Parse(cmd.ReviewId);
        var review = await reviews.GetByIdAsync(id, ct)
                     ?? throw NotFoundException.For("Review", cmd.ReviewId);

        review.AddComment(new Author(cmd.ReaderId, cmd.AuthorName), cmd.Text);
        await reviews.UpdateAsync(review, ct);
    }
}

public sealed class AddCommentCommandValidator : AbstractValidator<AddCommentCommand>
{
    public AddCommentCommandValidator()
    {
        RuleFor(x => x.ReviewId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("ReviewId must be a valid ObjectId");
        RuleFor(x => x.AuthorName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Text).NotEmpty().MaximumLength(1000);
    }
}

// ------------------------------------------------- RefreshBookTitle (подія) ---

/// <summary>
/// Оновлює денормалізовану назву книги в усіх відгуках.
/// У Лаб. 08 цю команду викличе обробник події <c>BookUpdated</c> з Каталогу;
/// зараз вона доступна як ендпоінт для демонстрації політики дублювання.
/// </summary>
public record RefreshBookTitleCommand(long BookId, string NewTitle) : ICommand<long>;

public sealed class RefreshBookTitleCommandHandler(IReviewRepository reviews)
    : IRequestHandler<RefreshBookTitleCommand, long>
{
    public Task<long> Handle(RefreshBookTitleCommand cmd, CancellationToken ct)
        => reviews.RefreshBookTitleAsync(cmd.BookId, cmd.NewTitle, ct);
}

public sealed class RefreshBookTitleCommandValidator : AbstractValidator<RefreshBookTitleCommand>
{
    public RefreshBookTitleCommandValidator()
    {
        RuleFor(x => x.BookId).GreaterThan(0);
        RuleFor(x => x.NewTitle).NotEmpty().MaximumLength(300);
    }
}
