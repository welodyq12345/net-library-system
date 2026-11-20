using System.Data;
using AutoMapper;
using Library.Lending.Bll.Abstractions;
using Library.Lending.Bll.Dtos;
using Library.Lending.Dal.Abstractions;
using Library.Lending.Domain.Entities;
using Library.Lending.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Library.Lending.Bll.Services;

/// <summary>Сервіс читачів. Працює через Unit of Work і репозиторій на чистому ADO.NET.</summary>
public sealed class ReaderService : IReaderService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;
    private readonly ILogger<ReaderService> _logger;

    public ReaderService(IUnitOfWork uow, IMapper mapper, ILogger<ReaderService> logger)
    {
        _uow = uow;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<ReaderDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var reader = await _uow.Readers.GetByIdAsync(id, ct)
                     ?? throw NotFoundException.For("Reader", id);
        return _mapper.Map<ReaderDto>(reader);
    }

    public async Task<IReadOnlyList<ReaderDto>> GetAllAsync(CancellationToken ct = default)
    {
        var readers = await _uow.Readers.GetAllAsync(ct);
        return readers.Select(_mapper.Map<ReaderDto>).ToList();
    }

    /// <summary>
    /// Створення читача. Бізнес-правило, для якого потрібен стан із БД:
    /// унікальність email. Перевіряємо заздалегідь, щоб повернути зрозумілий
    /// 409, а не чекати на порушення UNIQUE-індексу.
    /// </summary>
    public async Task<ReaderDto> CreateAsync(CreateReaderDto input, CancellationToken ct = default)
    {
        if (await _uow.Readers.ExistsByEmailAsync(input.Email, ct))
            throw new BusinessConflictException($"Reader with email '{input.Email}' already exists");

        if (await _uow.Readers.GetByCardNumberAsync(input.CardNumber, ct) is not null)
            throw new BusinessConflictException($"Reader with card number '{input.CardNumber}' already exists");

        var entity = _mapper.Map<Reader>(input);
        entity.CreatedBy = "lending-api";

        await _uow.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        long id;
        try
        {
            id = await _uow.Readers.AddAsync(entity, ct);
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        _logger.LogInformation("Reader {ReaderId} created with card {CardNumber}", id, input.CardNumber);

        var saved = await _uow.Readers.GetByIdAsync(id, ct)
                    ?? throw NotFoundException.For("Reader", id);
        return _mapper.Map<ReaderDto>(saved);
    }

    public async Task SetBlockedAsync(long id, bool isBlocked, CancellationToken ct = default)
    {
        if (!await _uow.Readers.SetBlockedAsync(id, isBlocked, "lending-api", ct))
            throw NotFoundException.For("Reader", id);

        _logger.LogInformation("Reader {ReaderId} blocked flag set to {IsBlocked}", id, isBlocked);
    }

    /// <summary>
    /// М'яке видалення: читача з історією видач фізично не видаляємо
    /// (у БД немає каскаду <c>Readers -> Loans</c>).
    /// </summary>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        if (!await _uow.Readers.SoftDeleteAsync(id, ct))
            throw NotFoundException.For("Reader", id);

        _logger.LogInformation("Reader {ReaderId} soft-deleted", id);
    }
}
