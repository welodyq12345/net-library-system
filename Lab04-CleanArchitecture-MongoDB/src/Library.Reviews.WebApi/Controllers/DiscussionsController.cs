using Library.Reviews.Application.Discussions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Library.Reviews.WebApi.Controllers;

/// <summary>
/// Обговорення книг. Окрема колекція MongoDB, на яку відгуки посилаються
/// за <c>ObjectId</c> (referencing).
/// </summary>
[Route("api/discussions")]
public sealed class DiscussionsController(ISender mediator) : ApiControllerBase(mediator)
{
    /// <summary>Обговорення за ідентифікатором разом із повідомленнями.</summary>
    /// <param name="id">ObjectId обговорення.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    /// <response code="404">Обговорення не існує.</response>
    [HttpGet("{id}")]
    [ProducesResponseType<DiscussionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiscussionDto>> GetById(string id, CancellationToken ct)
    {
        var discussion = await Mediator.Send(new GetDiscussionByIdQuery(id), ct);
        Response.Headers.ETag = $"\"{discussion.Version}\"";
        return Ok(discussion);
    }

    /// <summary>Усі обговорення книги.</summary>
    /// <param name="bookId">Ідентифікатор книги з Каталогу.</param>
    /// <param name="ct">Токен скасування запиту.</param>
    [HttpGet("by-book/{bookId:long}")]
    [ProducesResponseType<IReadOnlyList<DiscussionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DiscussionDto>>> GetByBook(
        long bookId, CancellationToken ct)
        => Ok(await Mediator.Send(new GetDiscussionsByBookQuery(bookId), ct));

    /// <summary>Створює обговорення.</summary>
    /// <response code="201">Створено.</response>
    [HttpPost]
    [ProducesResponseType<string>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<string>> Create(
        CreateDiscussionCommand command, CancellationToken ct)
    {
        var id = await Mediator.Send(command, ct);
        return CreatedAtAction(nameof(GetById), new { id }, id);
    }

    /// <summary>Додає повідомлення в обговорення.</summary>
    /// <response code="204">Повідомлення додано.</response>
    /// <response code="400">Обговорення закрите або некоректний replyTo.</response>
    [HttpPost("{id}/messages")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Post(string id, PostMessageBody body, CancellationToken ct)
    {
        await Mediator.Send(
            new PostDiscussionMessageCommand(id, body.ReaderId, body.AuthorName, body.Text, body.ReplyTo), ct);
        return NoContent();
    }

    /// <summary>Закриває обговорення.</summary>
    /// <response code="204">Закрито.</response>
    /// <response code="400">Обговорення вже закрите.</response>
    [HttpPost("{id}/close")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Close(string id, CloseBody? body, CancellationToken ct)
    {
        await Mediator.Send(new CloseDiscussionCommand(id, body?.Reason), ct);
        return NoContent();
    }
}

/// <summary>Тіло запиту додавання повідомлення.</summary>
public record PostMessageBody(long? ReaderId, string AuthorName, string Text, int? ReplyTo);

/// <summary>Тіло запиту закриття обговорення.</summary>
public record CloseBody(string? Reason);
