using AutoMapper;
using Library.Lending.Bll.Dtos;
using Library.Lending.Bll.Mapping;
using Library.Lending.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Library.Lending.Tests;

/// <summary>
/// Перевірка конфігурації AutoMapper. Крок C.1 Лаб. 02 вимагає валідувати
/// мапінг тестом <c>AssertConfigurationIsValid()</c>: він ловить забуті
/// властивості ще на етапі збірки тестів, а не в рантаймі.
/// </summary>
public class MappingProfileTests
{
    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<LendingProfile>(),
                                NullLoggerFactory.Instance).CreateMapper();

    [Fact]
    public void Configuration_IsValid()
    {
        var config = new MapperConfiguration(
            cfg => cfg.AddProfile<LendingProfile>(),
            NullLoggerFactory.Instance);

        // кидає AutoMapperConfigurationException, якщо якась властивість DTO
        // не має джерела в сутності
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Maps_Loan_WithItemsAndDetail()
    {
        var mapper = CreateMapper();

        var loan = new Loan
        {
            Id = 42,
            ReaderId = 7,
            Status = "Issued",
            LoanDate = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
            DueDate = new DateOnly(2026, 11, 15),
            FineAmount = 12.50m,
            Detail = new LoanDetail { LoanId = 42, PickupBranch = "Центральна філія", Comment = "коментар" }
        };
        loan.Items.Add(new LoanItem
        {
            LoanId = 42, BookId = 1, BookTitle = "Кобзар", BookAuthor = "Тарас Шевченко",
            DailyFee = 3.5m, Units = 2, Discount = 1m
        });

        var dto = mapper.Map<LoanDto>(loan);

        Assert.Equal(42, dto.Id);
        Assert.Equal("Issued", dto.Status);
        Assert.Equal(new DateOnly(2026, 11, 15), dto.DueDate);
        Assert.Equal(12.50m, dto.FineAmount);
        Assert.NotNull(dto.Detail);
        Assert.Equal("Центральна філія", dto.Detail!.PickupBranch);

        var item = Assert.Single(dto.Items);
        Assert.Equal("Кобзар", item.BookTitle);
        // DailyCharge = DailyFee * Units - Discount = 3.5 * 2 - 1 = 6.0
        Assert.Equal(6.0m, item.DailyCharge);
    }

    [Fact]
    public void Maps_CreateReaderDto_ToEntity()
    {
        var mapper = CreateMapper();

        var dto = new CreateReaderDto
        {
            CardNumber = "LC-000099",
            Email = "test@example.com",
            FullName = "Liam O'Brien",
            Phone = "+380501234567"
        };

        var entity = mapper.Map<Reader>(dto);

        Assert.Equal("LC-000099", entity.CardNumber);
        Assert.Equal("Liam O'Brien", entity.FullName);   // апостроф не губиться
        Assert.False(entity.IsBlocked);
        Assert.Equal(0, entity.Id);                      // Id призначає база
    }

    [Fact]
    public void Maps_Reader_ToDto()
    {
        var mapper = CreateMapper();

        var reader = new Reader
        {
            Id = 5, CardNumber = "LC-000005", Email = "a@b.com",
            FullName = "Олена Коваленко", IsBlocked = true,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var dto = mapper.Map<ReaderDto>(reader);

        Assert.Equal(5, dto.Id);
        Assert.Equal("Олена Коваленко", dto.FullName);
        Assert.True(dto.IsBlocked);
    }
}
