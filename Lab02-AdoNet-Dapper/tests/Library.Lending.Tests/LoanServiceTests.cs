using System.Data;
using AutoMapper;
using Library.Lending.Bll.Dtos;
using Library.Lending.Bll.Mapping;
using Library.Lending.Bll.Services;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Domain.Constants;
using Library.Lending.Domain.Entities;
using Library.Lending.Domain.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Library.Lending.Tests;

/// <summary>
/// Unit-тести бізнес-логіки: DAL і Unit of Work замокані (Moq), тому
/// тести не торкаються бази даних і перевіряють саме РІШЕННЯ сервісу.
/// Покрито «happy path» і негативні сценарії (валідація, бізнес-помилки).
/// </summary>
public class LoanServiceTests
{
    private readonly Mock<IUnitOfWork> _uow = new(MockBehavior.Strict);
    private readonly Mock<ILoanRepository> _loans = new(MockBehavior.Strict);
    private readonly Mock<IReaderRepository> _readers = new(MockBehavior.Strict);
    private readonly Mock<IBookRepository> _books = new(MockBehavior.Strict);
    private readonly Mock<IFinePaymentRepository> _fines = new(MockBehavior.Strict);
    private readonly IMapper _mapper;

    public LoanServiceTests()
    {
        _mapper = new MapperConfiguration(
            cfg => cfg.AddProfile<LendingProfile>(),
            NullLoggerFactory.Instance).CreateMapper();

        _uow.SetupGet(u => u.Loans).Returns(_loans.Object);
        _uow.SetupGet(u => u.Readers).Returns(_readers.Object);
        _uow.SetupGet(u => u.Books).Returns(_books.Object);
        _uow.SetupGet(u => u.FinePayments).Returns(_fines.Object);
    }

    private LoanService CreateSut() =>
        new(_uow.Object, _mapper, NullLogger<LoanService>.Instance);

    private void ExpectTransaction()
    {
        _uow.Setup(u => u.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(u => u.CommitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _uow.Setup(u => u.RollbackAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static Reader ActiveReader(long id = 1) => new()
    {
        Id = id, CardNumber = $"LC-{id:D6}", Email = $"r{id}@example.com",
        FullName = "Тестовий Читач", IsBlocked = false
    };

    private static Book AvailableBook(long id = 1, int copies = 5) => new()
    {
        Id = id, Title = "Кобзар", Author = "Тарас Шевченко",
        DailyFee = 3.5m, CopiesAvailable = copies
    };

    private static Loan LoanWith(string status, long id = 10, int items = 1)
    {
        var loan = new Loan
        {
            Id = id, ReaderId = 1, Status = status,
            LoanDate = DateTime.UtcNow.AddDays(-1),
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14))
        };
        for (var i = 0; i < items; i++)
            loan.Items.Add(new LoanItem
            {
                LoanId = id, BookId = i + 1, BookTitle = "Кобзар",
                BookAuthor = "Тарас Шевченко", DailyFee = 3.5m, Units = 1
            });
        return loan;
    }

    // ------------------------------------------------------------ GetById ---

    [Fact]
    public async Task GetById_ReturnsDto_WhenLoanExists()
    {
        var loan = LoanWith(LoanStatus.Issued, 10, items: 2);
        _loans.Setup(r => r.GetWithItemsAsync(10, It.IsAny<CancellationToken>()))
              .ReturnsAsync(loan);

        var dto = await CreateSut().GetByIdAsync(10);

        Assert.Equal(10, dto.Id);
        Assert.Equal(LoanStatus.Issued, dto.Status);
        Assert.Equal(2, dto.Items.Count);
    }

    /// <summary>
    /// Контрольна точка Кроку C Лаб. 02:
    /// «підтвердження неіснуючої видачі → NotFoundException».
    /// </summary>
    [Fact]
    public async Task GetById_Throws_NotFound_WhenLoanMissing()
    {
        _loans.Setup(r => r.GetWithItemsAsync(999, It.IsAny<CancellationToken>()))
              .ReturnsAsync((Loan?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateSut().GetByIdAsync(999));

        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public async Task ConfirmAndIssue_Throws_NotFound_WhenLoanMissing()
    {
        ExpectTransaction();
        _loans.Setup(r => r.GetWithItemsAsync(999, It.IsAny<CancellationToken>()))
              .ReturnsAsync((Loan?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateSut().ConfirmAndIssueAsync(999));

        // транзакцію мусить бути відкочено
        _uow.Verify(u => u.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------- Create ---

    [Fact]
    public async Task Create_Throws_NotFound_WhenReaderMissing()
    {
        _readers.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Reader?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateSut().CreateAsync(new CreateLoanDto
        {
            ReaderId = 5,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            PickupBranch = "Центральна філія",
            Items = [new CreateLoanItemDto { BookId = 1, Units = 1 }]
        }));

        _uow.Verify(u => u.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()),
                    Times.Never);
    }

    [Fact]
    public async Task Create_Throws_Conflict_WhenReaderBlocked()
    {
        var blocked = ActiveReader(5);
        blocked.IsBlocked = true;
        _readers.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(blocked);

        var ex = await Assert.ThrowsAsync<BusinessConflictException>(
            () => CreateSut().CreateAsync(new CreateLoanDto
            {
                ReaderId = 5,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                PickupBranch = "Центральна філія",
                Items = [new CreateLoanItemDto { BookId = 1, Units = 1 }]
            }));

        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_Throws_Validation_WhenDueDateInPast()
    {
        _readers.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ActiveReader());

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => CreateSut().CreateAsync(new CreateLoanDto
            {
                ReaderId = 1,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                PickupBranch = "Центральна філія",
                Items = [new CreateLoanItemDto { BookId = 1, Units = 1 }]
            }));

        Assert.True(ex.Errors.ContainsKey("DueDate"));
    }

    /// <summary>Брак примірників виявляється ДО початку транзакції.</summary>
    [Fact]
    public async Task Create_Throws_Conflict_WhenNotEnoughCopies()
    {
        _readers.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ActiveReader());
        _books.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
              .ReturnsAsync(AvailableBook(1, copies: 2));

        var ex = await Assert.ThrowsAsync<BusinessConflictException>(
            () => CreateSut().CreateAsync(new CreateLoanDto
            {
                ReaderId = 1,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                PickupBranch = "Центральна філія",
                Items = [new CreateLoanItemDto { BookId = 1, Units = 10 }]
            }));

        Assert.Contains("only 2 copies", ex.Message);
        _uow.Verify(u => u.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()),
                    Times.Never);
    }

    /// <summary>Дублікати тієї самої книги у запиті склеюються в одну позицію.</summary>
    [Fact]
    public async Task Create_MergesDuplicateBooks_IntoSingleItem()
    {
        ExpectTransaction();
        _readers.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ActiveReader());
        _books.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
              .ReturnsAsync(AvailableBook(1, copies: 5));

        IEnumerable<(long BookId, int Units)>? captured = null;
        _loans.Setup(r => r.CreateViaProcedureAsync(
                  1, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<string?>(),
                  It.IsAny<IEnumerable<(long, int)>>(), It.IsAny<string?>(),
                  It.IsAny<CancellationToken>()))
              .Callback((long _, DateOnly _, string _, string? _,
                         IEnumerable<(long BookId, int Units)> items, string? _, CancellationToken _) =>
                         captured = items.ToList())
              .ReturnsAsync(77L);

        _loans.Setup(r => r.GetWithItemsAsync(77, It.IsAny<CancellationToken>()))
              .ReturnsAsync(LoanWith(LoanStatus.Submitted, 77));

        await CreateSut().CreateAsync(new CreateLoanDto
        {
            ReaderId = 1,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            PickupBranch = "Центральна філія",
            Items =
            [
                new CreateLoanItemDto { BookId = 1, Units = 2 },
                new CreateLoanItemDto { BookId = 1, Units = 3 }
            ]
        });

        Assert.NotNull(captured);
        var single = Assert.Single(captured!);
        Assert.Equal(1, single.BookId);
        Assert.Equal(5, single.Units);          // 2 + 3 склеєно
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------- ConfirmAndIssue (happy) ---

    [Fact]
    public async Task ConfirmAndIssue_CallsBothProcedures_AndCommits()
    {
        ExpectTransaction();
        var loan = LoanWith(LoanStatus.Submitted, 10);

        _loans.Setup(r => r.GetWithItemsAsync(10, It.IsAny<CancellationToken>()))
              .ReturnsAsync(loan);
        _loans.Setup(r => r.ConfirmCopiesAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        _loans.Setup(r => r.IssueAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var dto = await CreateSut().ConfirmAndIssueAsync(10);

        _loans.Verify(r => r.ConfirmCopiesAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _loans.Verify(r => r.IssueAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(10, dto.Id);
    }

    [Fact]
    public async Task ConfirmAndIssue_Throws_Conflict_WhenLoanHasNoItems()
    {
        ExpectTransaction();
        _loans.Setup(r => r.GetWithItemsAsync(10, It.IsAny<CancellationToken>()))
              .ReturnsAsync(LoanWith(LoanStatus.Submitted, 10, items: 0));

        await Assert.ThrowsAsync<BusinessConflictException>(
            () => CreateSut().ConfirmAndIssueAsync(10));

        _uow.Verify(u => u.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// КЛЮЧОВИЙ тест транзакційності: помилка в ДРУГОМУ кроці операції
    /// (брак примірників у процедурі видачі) мусить відкотити транзакцію,
    /// тому зміни першого кроку (підтвердження статусу) не збережуться.
    /// </summary>
    [Fact]
    public async Task ConfirmAndIssue_RollsBack_WhenSecondStepFails()
    {
        ExpectTransaction();
        _loans.Setup(r => r.GetWithItemsAsync(10, It.IsAny<CancellationToken>()))
              .ReturnsAsync(LoanWith(LoanStatus.Submitted, 10));
        _loans.Setup(r => r.ConfirmCopiesAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        _loans.Setup(r => r.IssueAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new BusinessConflictException("Not enough copies available to issue the loan"));

        await Assert.ThrowsAsync<BusinessConflictException>(
            () => CreateSut().ConfirmAndIssueAsync(10));

        _loans.Verify(r => r.ConfirmCopiesAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------- GetByReader ---

    [Fact]
    public async Task GetByReader_Throws_Validation_OnUnknownStatus()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => CreateSut().GetByReaderAsync(1, "NoSuchStatus"));

        Assert.True(ex.Errors.ContainsKey("status"));
    }

    [Fact]
    public async Task GetByReader_Throws_NotFound_WhenReaderMissing()
    {
        _readers.Setup(r => r.GetByIdAsync(42, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Reader?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateSut().GetByReaderAsync(42, null));
    }

    // ----------------------------------------------------------- PayFine ---

    /// <summary>Ідемпотентність: наявний платіж повертається, новий не створюється.</summary>
    [Fact]
    public async Task PayFine_ReturnsExistingPayment_OnIdempotentReplay()
    {
        var loan = LoanWith(LoanStatus.Returned, 10);
        loan.FineAmount = 50m;

        _loans.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(loan);
        _fines.Setup(r => r.FindByIdempotencyKeyAsync("key-1", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new FinePayment { Id = 555, LoanId = 10, Amount = 50m, Method = "Card" });

        var dto = await CreateSut().PayFineAsync(10, new PayFineDto
        {
            Amount = 50m, Method = "Card", IdempotencyKey = "key-1"
        });

        Assert.Equal(555, dto.Id);
        _fines.Verify(r => r.AddAsync(It.IsAny<FinePayment>(), It.IsAny<CancellationToken>()), Times.Never);
        _uow.Verify(u => u.BeginTransactionAsync(It.IsAny<IsolationLevel>(), It.IsAny<CancellationToken>()),
                    Times.Never);
    }

    [Fact]
    public async Task PayFine_Throws_Conflict_WhenNoOutstandingFine()
    {
        var loan = LoanWith(LoanStatus.Returned, 10);
        loan.FineAmount = 0m;

        _loans.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(loan);

        var ex = await Assert.ThrowsAsync<BusinessConflictException>(
            () => CreateSut().PayFineAsync(10, new PayFineDto { Amount = 10m, Method = "Cash" }));

        Assert.Contains("no outstanding fine", ex.Message);
    }

    [Fact]
    public async Task PayFine_Throws_Conflict_WhenAmountExceedsOutstanding()
    {
        var loan = LoanWith(LoanStatus.Returned, 10);
        loan.FineAmount = 50m;

        _loans.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(loan);
        _fines.Setup(r => r.GetTotalPaidAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(40m);

        var ex = await Assert.ThrowsAsync<BusinessConflictException>(
            () => CreateSut().PayFineAsync(10, new PayFineDto { Amount = 30m, Method = "Cash" }));

        Assert.Contains("exceeds the outstanding fine", ex.Message);
    }

    [Fact]
    public async Task PayFine_Throws_Conflict_WhenAlreadyFullyPaid()
    {
        var loan = LoanWith(LoanStatus.Returned, 10);
        loan.FineAmount = 50m;

        _loans.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(loan);
        _fines.Setup(r => r.GetTotalPaidAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(50m);

        var ex = await Assert.ThrowsAsync<BusinessConflictException>(
            () => CreateSut().PayFineAsync(10, new PayFineDto { Amount = 5m, Method = "Cash" }));

        Assert.Contains("already fully paid", ex.Message);
    }

    // ------------------------------------------------------------- Return ---

    [Fact]
    public async Task Return_Throws_Validation_WhenReturnDateInFuture()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => CreateSut().ReturnAsync(10, new ReturnLoanDto
            {
                ReturnDate = DateTime.UtcNow.AddDays(30)
            }));

        Assert.True(ex.Errors.ContainsKey("ReturnDate"));
    }

    [Fact]
    public async Task Return_Commits_AndReturnsUpdatedLoan()
    {
        ExpectTransaction();
        _loans.Setup(r => r.ReturnAsync(10, It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(12.5m);

        var returned = LoanWith(LoanStatus.Returned, 10);
        returned.FineAmount = 12.5m;
        _loans.Setup(r => r.GetWithItemsAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(returned);

        var dto = await CreateSut().ReturnAsync(10, new ReturnLoanDto());

        Assert.Equal(LoanStatus.Returned, dto.Status);
        Assert.Equal(12.5m, dto.FineAmount);
        _uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
