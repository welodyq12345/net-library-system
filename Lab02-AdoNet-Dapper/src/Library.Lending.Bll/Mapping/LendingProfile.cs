using AutoMapper;
using Library.Lending.Bll.Dtos;
using Library.Lending.Domain.Entities;

namespace Library.Lending.Bll.Mapping;

/// <summary>
/// Профілі мапінгу AutoMapper. Зберігаються в BLL централізовано; коректність
/// конфігурації перевіряється тестом <c>AssertConfigurationIsValid()</c>
/// (див. <c>Library.Lending.Tests/MappingProfileTests.cs</c>).
/// </summary>
public sealed class LendingProfile : Profile
{
    public LendingProfile()
    {
        CreateMap<Reader, ReaderDto>();

        CreateMap<Book, BookDto>();

        CreateMap<LoanItem, LoanItemDto>();          // DailyCharge — обчислювана властивість сутності

        CreateMap<LoanDetail, LoanDetailDto>();

        CreateMap<Loan, LoanDto>();

        CreateMap<LoanStatusHistoryEntry, LoanStatusHistoryDto>();

        CreateMap<LoanSummary, LoanSummaryDto>();

        CreateMap<FinePayment, FinePaymentDto>();

        /* вхідні DTO -> сутності.
           Поля, які задає БАЗА (ідентифікатор, аудитні колонки, soft delete)
           або СЕРВІС, явно позначаємо Ignore: інакше AssertConfigurationIsValid()
           справедливо скаржиться на незамаплені властивості призначення.
           Явний Ignore — це задокументоване рішення, а не приховування помилки. */
        CreateMap<CreateReaderDto, Reader>()
            .ForMember(d => d.Id,        o => o.Ignore())   // IDENTITY у dbo.Readers
            .ForMember(d => d.IsBlocked, o => o.Ignore())   // DEFAULT 0
            .ForMember(d => d.CreatedAt, o => o.Ignore())   // DEFAULT SYSUTCDATETIME()
            .ForMember(d => d.CreatedBy, o => o.Ignore())   // задає сервіс
            .ForMember(d => d.UpdatedAt, o => o.Ignore())
            .ForMember(d => d.UpdatedBy, o => o.Ignore())
            .ForMember(d => d.IsDeleted, o => o.Ignore());  // DEFAULT 0

        CreateMap<SyncBookDto, Book>()
            .ForMember(d => d.SyncedAt, o => o.Ignore());   // SYSUTCDATETIME() у MERGE
    }
}
