using FluentValidation;
using Library.Reviews.Application.Common.Interfaces;
using Library.Reviews.Application.Common.Messaging;
using Library.Reviews.Application.Common.Models;
using Library.Reviews.Domain.Exceptions;
using Library.Reviews.Domain.ValueObjects;
using MediatR;
using MongoDB.Bson;

namespace Library.Reviews.Application.Reviews.Queries;

/* =============================================================================
   ЗАПИТИ (сторона читання CQRS).

   Обробник запиту НЕ звертається до MongoDB напряму — Application не залежить
   від Infrastructure. Він викликає IReviewQueries, а реалізація в Infrastructure
   використовує проєкції та aggregation pipeline.
   ============================================================================= */

// ----------------------------------------------------------- GetReviewById ---

public record GetReviewByIdQuery(string Id) : IQuery<ReviewDetailsDto>;

public sealed class GetReviewByIdQueryHandler(IReviewQueries queries)
    : IRequestHandler<GetReviewByIdQuery, ReviewDetailsDto>
{
    public async Task<ReviewDetailsDto> Handle(GetReviewByIdQuery q, CancellationToken ct)
        => await queries.GetByIdAsync(q.Id, ct)
           ?? throw NotFoundException.For("Review", q.Id);
}

public sealed class GetReviewByIdQueryValidator : AbstractValidator<GetReviewByIdQuery>
{
    public GetReviewByIdQueryValidator()
        => RuleFor(x => x.Id)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Id must be a valid ObjectId");
}

// --------------------------------------------------------- GetReviewsList ---

public record GetReviewsListQuery(
    long? BookId = null,
    int? MinRating = null,
    int Page = 1,
    int PageSize = 20) : IQuery<PagedResult<ReviewDto>>;

public sealed class GetReviewsListQueryHandler(IReviewQueries queries)
    : IRequestHandler<GetReviewsListQuery, PagedResult<ReviewDto>>
{
    public Task<PagedResult<ReviewDto>> Handle(GetReviewsListQuery q, CancellationToken ct)
        => queries.GetListAsync(
            q.BookId, q.MinRating, Math.Max(q.Page, 1), Math.Clamp(q.PageSize, 1, 100), ct);
}

public sealed class GetReviewsListQueryValidator : AbstractValidator<GetReviewsListQuery>
{
    public GetReviewsListQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.BookId).GreaterThan(0).When(x => x.BookId.HasValue);
        RuleFor(x => x.MinRating)
            .InclusiveBetween(Rating.Min, Rating.Max)
            .When(x => x.MinRating.HasValue);
    }
}

// ------------------------------------------------------ GetReviewsByCursor ---

public record GetReviewsByCursorQuery(long? BookId, string? Cursor, int PageSize = 20)
    : IQuery<CursorPage<ReviewDto>>;

public sealed class GetReviewsByCursorQueryHandler(IReviewQueries queries)
    : IRequestHandler<GetReviewsByCursorQuery, CursorPage<ReviewDto>>
{
    public Task<CursorPage<ReviewDto>> Handle(GetReviewsByCursorQuery q, CancellationToken ct)
        => queries.GetPageByCursorAsync(q.BookId, q.Cursor, Math.Clamp(q.PageSize, 1, 100), ct);
}

public sealed class GetReviewsByCursorQueryValidator : AbstractValidator<GetReviewsByCursorQuery>
{
    public GetReviewsByCursorQueryValidator()
    {
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Cursor)
            .Must(c => ObjectId.TryParse(c, out _))
            .When(x => !string.IsNullOrEmpty(x.Cursor))
            .WithMessage("Cursor must be a valid ObjectId");
    }
}

// ---------------------------------------------------------- GetBookRatings ---

public record GetBookRatingsQuery : IQuery<IReadOnlyList<BookRatingDto>>;

public sealed class GetBookRatingsQueryHandler(IReviewQueries queries)
    : IRequestHandler<GetBookRatingsQuery, IReadOnlyList<BookRatingDto>>
{
    public Task<IReadOnlyList<BookRatingDto>> Handle(GetBookRatingsQuery q, CancellationToken ct)
        => queries.GetBookRatingsAsync(ct);
}

// ------------------------------------------------------------ SearchReviews ---

public record SearchReviewsQuery(string Term, int Limit = 20) : IQuery<IReadOnlyList<ReviewDto>>;

public sealed class SearchReviewsQueryHandler(IReviewQueries queries)
    : IRequestHandler<SearchReviewsQuery, IReadOnlyList<ReviewDto>>
{
    public Task<IReadOnlyList<ReviewDto>> Handle(SearchReviewsQuery q, CancellationToken ct)
        => queries.SearchAsync(q.Term, Math.Clamp(q.Limit, 1, 100), ct);
}

public sealed class SearchReviewsQueryValidator : AbstractValidator<SearchReviewsQuery>
{
    public SearchReviewsQueryValidator()
    {
        RuleFor(x => x.Term).NotEmpty().MinimumLength(2).MaximumLength(100);
        RuleFor(x => x.Limit).InclusiveBetween(1, 100);
    }
}
