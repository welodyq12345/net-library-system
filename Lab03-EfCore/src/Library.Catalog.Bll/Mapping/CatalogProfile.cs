using AutoMapper;
using Library.Catalog.Bll.Dtos;
using Library.Catalog.Domain.Entities;

namespace Library.Catalog.Bll.Mapping;

/// <summary>
/// Профілі мапінгу AutoMapper. Підключаються централізовано в Program.cs;
/// конфігурація перевіряється тестом <c>AssertConfigurationIsValid()</c>.
/// </summary>
public sealed class CatalogProfile : Profile
{
    public CatalogProfile()
    {
        CreateMap<Author, AuthorDto>();

        CreateMap<Author, AuthorWithBooksDto>();

        CreateMap<Genre, GenreDto>();

        CreateMap<BookDetail, BookDetailDto>();

        CreateMap<BookCopy, BookCopyDto>();

        CreateMap<Book, BookListItemDto>()
            .ForCtorParam(nameof(BookListItemDto.AuthorName),
                o => o.MapFrom(b => b.Author != null ? b.Author.FullName : ""))
            .ForCtorParam(nameof(BookListItemDto.Genres),
                o => o.MapFrom(b => b.BookGenres
                    .Where(bg => bg.Genre != null)
                    .Select(bg => bg.Genre.Name)
                    .ToList()));

        CreateMap<Book, BookDto>()
            .ForCtorParam(nameof(BookDto.AuthorName),
                o => o.MapFrom(b => b.Author != null ? b.Author.FullName : ""))
            .ForCtorParam(nameof(BookDto.Genres),
                o => o.MapFrom(b => b.BookGenres
                    .Where(bg => bg.Genre != null)
                    .Select(bg => bg.Genre.Name)
                    .ToList()));
    }
}
