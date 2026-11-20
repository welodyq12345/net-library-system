using System.Reflection;
using FluentValidation;
using Library.Reviews.Application.Common.Behaviors;
using Library.Reviews.Application.Common.Models;
using Library.Reviews.Application.Reviews.Commands;
using Library.Reviews.Application.Reviews.Queries;
using Library.Reviews.Domain.Common;
using Library.Reviews.Domain.Entities;
using Library.Reviews.Domain.Exceptions;
using Library.Reviews.Domain.Interfaces;
using Library.Reviews.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Moq;

namespace Library.Reviews.Tests;

/// <summary>
/// Перевірка ПРАВИЛА ЗАЛЕЖНОСТЕЙ Clean Architecture автоматичним тестом,
/// а не на око.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(BaseEntity).Assembly;
    private static readonly Assembly Application = typeof(GetReviewsListQuery).Assembly;

    /// <summary>
    /// Головний критерій приймання: Domain не посилається на жоден інший
    /// шар рішення.
    /// </summary>
    [Fact]
    public void Domain_DoesNotReferenceOtherLayers()
    {
        var referenced = Domain.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("Library.Reviews.Application", referenced);
        Assert.DoesNotContain("Library.Reviews.Infrastructure", referenced);
        Assert.DoesNotContain("Library.Reviews.WebApi", referenced);
    }

    /// <summary>
    /// Domain знає про <c>MongoDB.Bson</c> (пакет ТИПІВ, потрібен ObjectId),
    /// але не про драйвер доступу до бази.
    /// </summary>
    [Fact]
    public void Domain_DoesNotReferenceMongoDriver()
    {
        var referenced = Domain.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("MongoDB.Driver", referenced);
        Assert.Contains("MongoDB.Bson", referenced);
    }

    /// <summary>Domain не знає ні про MediatR, ні про ASP.NET Core.</summary>
    [Fact]
    public void Domain_HasNoFrameworkDependencies()
    {
        var referenced = Domain.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("MediatR", referenced);
        Assert.DoesNotContain("FluentValidation", referenced);
        Assert.DoesNotContain("Microsoft.AspNetCore", referenced);
    }

    /// <summary>Application не залежить від Infrastructure — лише від Domain.</summary>
    [Fact]
    public void Application_DoesNotReferenceInfrastructureOrWebApi()
    {
        var referenced = Application.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("Library.Reviews.Infrastructure", referenced);
        Assert.DoesNotContain("Library.Reviews.WebApi", referenced);
        Assert.Contains("Library.Reviews.Domain", referenced);
    }

    /// <summary>
    /// Жоден обробник в Application не використовує <c>IMongoCollection</c>
    /// чи інші типи драйвера — читання йде через інтерфейс <c>IReviewQueries</c>.
    /// </summary>
    [Fact]
    public void Application_DoesNotReferenceMongoDriver()
    {
        var referenced = Application.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("MongoDB.Driver", referenced);
    }

    /// <summary>Кожна команда й кожен запит мають рівно один обробник.</summary>
    [Fact]
    public void EveryRequest_HasExactlyOneHandler()
    {
        var requests = Application.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && t.GetInterfaces().Any(i => i.IsGenericType
                            && i.GetGenericTypeDefinition() == typeof(IRequest<>)))
            .ToList();

        Assert.NotEmpty(requests);

        foreach (var request in requests)
        {
            var responseType = request.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                .GetGenericArguments()[0];

            var handlerType = typeof(IRequestHandler<,>).MakeGenericType(request, responseType);
            var handlers = Application.GetTypes()
                .Where(t => t is { IsAbstract: false } && handlerType.IsAssignableFrom(t))
                .ToList();

            Assert.True(handlers.Count == 1,
                $"{request.Name}: очікувався рівно 1 обробник, знайдено {handlers.Count}");
        }
    }

    /// <summary>Усі чотири Pipeline Behaviors присутні в Application.</summary>
    [Fact]
    public void AllPipelineBehaviors_Exist()
    {
        var behaviors = Application.GetTypes()
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>)))
            .Select(t => t.Name)
            .ToList();

        Assert.Contains("ValidationBehavior`2", behaviors);
        Assert.Contains("LoggingBehavior`2", behaviors);
        Assert.Contains("PerformanceBehavior`2", behaviors);
        Assert.Contains("ExceptionHandlingBehavior`2", behaviors);
    }
}

/// <summary>Тести ValidationBehavior — ключова механіка Частини 4 методички.</summary>
public class ValidationBehaviorTests
{
    private sealed record Probe(int Value) : IRequest<string>;

    private sealed class ProbeValidator : AbstractValidator<Probe>
    {
        public ProbeValidator() => RuleFor(x => x.Value).InclusiveBetween(1, 5);
    }

    private sealed class SecondProbeValidator : AbstractValidator<Probe>
    {
        public SecondProbeValidator()
            => RuleFor(x => x.Value).Must(v => v % 2 != 0).WithMessage("Значення не може бути парним");
    }

    private static ValidationBehavior<Probe, string> Create(params IValidator<Probe>[] validators)
        => new(validators, NullLogger<ValidationBehavior<Probe, string>>.Instance);

    /// <summary>
    /// Контрольна точка: при помилці валідації обробник НЕ викликається.
    /// </summary>
    [Fact]
    public async Task Handle_DoesNotCallHandler_WhenValidationFails()
    {
        var behavior = Create(new ProbeValidator());
        var handlerCalled = false;

        await Assert.ThrowsAsync<ValidationException>(() =>
            behavior.Handle(new Probe(10), _ =>
            {
                handlerCalled = true;
                return Task.FromResult("ok");
            }, CancellationToken.None));

        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task Handle_CallsHandler_WhenValid()
    {
        var behavior = Create(new ProbeValidator());

        var result = await behavior.Handle(new Probe(3), _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task Handle_CallsHandler_WhenNoValidatorsRegistered()
    {
        var behavior = Create();

        var result = await behavior.Handle(new Probe(999), _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", result);
    }

    /// <summary>
    /// Помилки ВСІХ валідаторів збираються разом і повертаються одним
    /// винятком, а не по одній.
    /// </summary>
    [Fact]
    public async Task Handle_CollectsFailuresFromAllValidators()
    {
        var behavior = Create(new ProbeValidator(), new SecondProbeValidator());

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            behavior.Handle(new Probe(0), _ => Task.FromResult("ok"), CancellationToken.None));

        var messages = ex.Errors.Select(e => e.ErrorMessage).ToList();

        // рівно дві помилки: по одній від кожного валідатора, без дублювання
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Contains("1") && m.Contains("5"));
        Assert.Contains(messages, m => m.Contains("парним"));
    }
}

/// <summary>Тести обробників CQRS із замоканими доменними інтерфейсами.</summary>
public class HandlerTests
{
    private readonly Mock<IReviewRepository> _reviews = new(MockBehavior.Strict);

    [Fact]
    public async Task CreateReviewCommandHandler_AddsReview_AndReturnsStringId()
    {
        Review? captured = null;
        _reviews.Setup(r => r.AddAsync(It.IsAny<Review>(), It.IsAny<CancellationToken>()))
                .Callback((Review r, CancellationToken _) => captured = r)
                .Returns(Task.CompletedTask);

        var handler = new CreateReviewCommandHandler(_reviews.Object);
        var id = await handler.Handle(
            new CreateReviewCommand(1, "Кобзар", 7, "Олена", 5, "текст"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(24, id.Length);                        // рядковий ObjectId
        Assert.Equal(captured!.Id.ToString(), id);
        Assert.Equal(5, captured.Rating.Value);
        Assert.Equal(7, captured.Author.ReaderId);
    }

    [Fact]
    public async Task UpdateReviewCommandHandler_Throws_NotFound()
    {
        _reviews.Setup(r => r.GetByIdAsync(It.IsAny<ObjectId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Review?)null);

        var handler = new UpdateReviewCommandHandler(_reviews.Object);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(
            new UpdateReviewCommand(ObjectId.GenerateNewId().ToString(), 4, "x", null),
            CancellationToken.None));
    }

    /// <summary>Перевірка If-Match: невідповідність версій -> ConflictException -> 409.</summary>
    [Fact]
    public async Task UpdateReviewCommandHandler_Throws_Conflict_OnVersionMismatch()
    {
        var review = new Review(new BookRef(1, "Кобзар"), new Author(1, "Олена"), 5, "текст");
        review.BumpVersion();                                // версія стала 1

        _reviews.Setup(r => r.GetByIdAsync(review.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(review);

        var handler = new UpdateReviewCommandHandler(_reviews.Object);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => handler.Handle(
            new UpdateReviewCommand(review.Id.ToString(), 4, "нове", ExpectedVersion: 0),
            CancellationToken.None));

        Assert.Contains("version", ex.Message);
        _reviews.Verify(r => r.UpdateAsync(It.IsAny<Review>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteReviewCommandHandler_Throws_NotFound_WhenNothingDeleted()
    {
        _reviews.Setup(r => r.DeleteAsync(It.IsAny<ObjectId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

        var handler = new DeleteReviewCommandHandler(_reviews.Object);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(
            new DeleteReviewCommand(ObjectId.GenerateNewId().ToString()), CancellationToken.None));
    }

    [Fact]
    public async Task AddCommentCommandHandler_AddsEmbeddedComment()
    {
        var review = new Review(new BookRef(1, "Кобзар"), new Author(1, "Олена"), 5, "текст");

        _reviews.Setup(r => r.GetByIdAsync(review.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(review);
        _reviews.Setup(r => r.UpdateAsync(review, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var handler = new AddCommentCommandHandler(_reviews.Object);
        await handler.Handle(
            new AddCommentCommand(review.Id.ToString(), null, "Гість", "Дякую"),
            CancellationToken.None);

        Assert.Single(review.Comments);
        _reviews.VerifyAll();
    }
}

/// <summary>Тести валідаторів команд і запитів.</summary>
public class ValidatorTests
{
    private static readonly Mock<IReviewRepository> Repo = new();

    /// <remarks>
    /// Валідатор містить АСИНХРОННЕ правило (перевірка «один відгук від читача»
    /// робить запит до репозиторію), тому FluentValidation дозволяє викликати
    /// лише <c>ValidateAsync</c> — синхронний <c>Validate</c> кидає
    /// <c>AsyncValidatorInvokedSynchronouslyException</c>.
    /// </remarks>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task CreateReviewCommandValidator_ChecksRatingRange(int rating, bool expectedValid)
    {
        Repo.Setup(r => r.ExistsAsync(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var validator = new CreateReviewCommandValidator(Repo.Object);
        var result = await validator.ValidateAsync(
            new CreateReviewCommand(1, "Кобзар", 1, "Олена", rating, "t"));

        Assert.Equal(expectedValid, result.Errors.All(e => e.PropertyName != "Rating"));
    }

    /// <summary>Асинхронне правило: читач уже залишав відгук -> помилка валідації.</summary>
    [Fact]
    public async Task CreateReviewCommandValidator_RejectsDuplicateReviewByTheSameReader()
    {
        var repo = new Mock<IReviewRepository>();
        repo.Setup(r => r.ExistsAsync(1, 7, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await new CreateReviewCommandValidator(repo.Object)
            .ValidateAsync(new CreateReviewCommand(1, "Кобзар", 7, "Олена", 5, "t"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("залишив відгук"));
    }

    /// <summary>Для анонімного відгуку (readerId = null) правило не застосовується.</summary>
    [Fact]
    public async Task CreateReviewCommandValidator_AllowsAnonymousDuplicate()
    {
        var repo = new Mock<IReviewRepository>();
        repo.Setup(r => r.ExistsAsync(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await new CreateReviewCommandValidator(repo.Object)
            .ValidateAsync(new CreateReviewCommand(1, "Кобзар", null, "Анонім", 5, "t"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("507f1f77bcf86cd799439011", true)]
    [InlineData("not-an-objectid", false)]
    [InlineData("", false)]
    public void GetReviewByIdQueryValidator_ChecksObjectIdFormat(string id, bool expectedValid)
    {
        var result = new GetReviewByIdQueryValidator().Validate(new GetReviewByIdQuery(id));
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Theory]
    [InlineData(1, 20, true)]
    [InlineData(0, 20, false)]
    [InlineData(1, 500, false)]
    public void GetReviewsListQueryValidator_ChecksPaging(int page, int pageSize, bool expectedValid)
    {
        var result = new GetReviewsListQueryValidator()
            .Validate(new GetReviewsListQuery(null, null, page, pageSize));
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void SearchReviewsQueryValidator_RejectsTooShortTerm()
        => Assert.False(new SearchReviewsQueryValidator().Validate(new SearchReviewsQuery("a")).IsValid);
}

/// <summary>Тести контракту посторінкової відповіді.</summary>
public class PagedResultTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(5, 20, 1)]
    [InlineData(21, 20, 2)]
    public void TotalPages_IsCeilingOfDivision(long total, int pageSize, int expected)
        => Assert.Equal(expected, new PagedResult<string>([], total, 1, pageSize).TotalPages);

    [Fact]
    public void CursorPage_HasMore_WhenCursorPresent()
    {
        Assert.True(new CursorPage<string>([], "abc").HasMore);
        Assert.False(new CursorPage<string>([], null).HasMore);
    }
}
