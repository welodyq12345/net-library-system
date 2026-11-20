using Library.Lending.Domain.Constants;
using Library.Lending.Domain.Entities;

namespace Library.Lending.Tests;

/// <summary>
/// Тести доменних правил, що не потребують ні бази, ні моків:
/// карта допустимих переходів статусів і обчислювані властивості сутностей.
/// </summary>
public class LoanStatusTests
{
    [Theory]
    [InlineData(LoanStatus.Submitted,          LoanStatus.CopiesConfirmed,    true)]
    [InlineData(LoanStatus.Submitted,          LoanStatus.AwaitingValidation, true)]
    [InlineData(LoanStatus.Submitted,          LoanStatus.Cancelled,          true)]
    [InlineData(LoanStatus.AwaitingValidation, LoanStatus.CopiesConfirmed,    true)]
    [InlineData(LoanStatus.CopiesConfirmed,    LoanStatus.Issued,             true)]
    [InlineData(LoanStatus.CopiesConfirmed,    LoanStatus.Cancelled,          true)]
    [InlineData(LoanStatus.Issued,             LoanStatus.Returned,           true)]
    public void AllowedTransitions_AreAccepted(string from, string to, bool expected)
        => Assert.Equal(expected, LoanStatus.CanTransition(from, to));

    [Theory]
    // книги вже на руках — скасувати не можна
    [InlineData(LoanStatus.Issued,    LoanStatus.Cancelled)]
    // видати можна лише після підтвердження примірників
    [InlineData(LoanStatus.Submitted, LoanStatus.Issued)]
    // завершені статуси — термінальні
    [InlineData(LoanStatus.Returned,  LoanStatus.Issued)]
    [InlineData(LoanStatus.Returned,  LoanStatus.Cancelled)]
    [InlineData(LoanStatus.Cancelled, LoanStatus.Issued)]
    [InlineData(LoanStatus.Cancelled, LoanStatus.CopiesConfirmed)]
    public void ForbiddenTransitions_AreRejected(string from, string to)
        => Assert.False(LoanStatus.CanTransition(from, to));

    [Fact]
    public void All_ContainsExactlySixStatuses()
        => Assert.Equal(6, LoanStatus.All.Count);

    [Fact]
    public void IsKnown_RejectsUnknownAndNull()
    {
        Assert.False(LoanStatus.IsKnown("Shipped"));
        Assert.False(LoanStatus.IsKnown(null));
        Assert.True(LoanStatus.IsKnown(LoanStatus.Issued));
    }

    [Fact]
    public void EveryStatus_HasTransitionMapEntry()
    {
        foreach (var status in LoanStatus.All)
            Assert.True(LoanStatus.AllowedTransitions.ContainsKey(status),
                        $"немає запису в карті переходів для статусу {status}");
    }

    // ---------------------------------------------- обчислювані властивості ---

    [Fact]
    public void LoanItem_DailyCharge_SubtractsDiscount()
    {
        var item = new LoanItem { DailyFee = 4.25m, Units = 3, Discount = 1.75m };

        // 4.25 * 3 - 1.75 = 11.00
        Assert.Equal(11.00m, item.DailyCharge);
    }

    [Fact]
    public void Loan_IsOverdue_OnlyWhenIssuedAndPastDue()
    {
        var due = new DateOnly(2026, 11, 10);
        var afterDue = new DateOnly(2026, 11, 20);
        var beforeDue = new DateOnly(2026, 11, 01);

        Assert.True(new Loan { Status = LoanStatus.Issued, DueDate = due }.IsOverdue(afterDue));
        Assert.False(new Loan { Status = LoanStatus.Issued, DueDate = due }.IsOverdue(beforeDue));

        // уже повернена або скасована видача простроченою не вважається
        Assert.False(new Loan { Status = LoanStatus.Returned, DueDate = due }.IsOverdue(afterDue));
        Assert.False(new Loan { Status = LoanStatus.Cancelled, DueDate = due }.IsOverdue(afterDue));
        Assert.False(new Loan { Status = LoanStatus.Submitted, DueDate = due }.IsOverdue(afterDue));
    }

    [Fact]
    public void SqlErrorCodes_MapToDocumentedValues()
    {
        // ці номери кидають збережувані процедури з Лаб. 01
        Assert.Equal(50001, SqlErrorCodes.NotFound);
        Assert.Equal(50002, SqlErrorCodes.InvalidTransition);
        Assert.Equal(50003, SqlErrorCodes.NotEnoughCopies);
        Assert.Equal(50004, SqlErrorCodes.BusinessRule);
    }
}
