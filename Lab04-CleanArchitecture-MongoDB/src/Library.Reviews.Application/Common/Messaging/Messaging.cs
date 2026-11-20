using MediatR;

namespace Library.Reviews.Application.Common.Messaging;

/* =============================================================================
   CQRS: розділення операцій ЗАПИСУ (Command) і ЧИТАННЯ (Query).

   Власні інтерфейси поверх MediatR потрібні, щоб:
     - намір операції було видно з типу (команда змінює стан, запит — ні);
     - можна було писати Pipeline Behaviors окремо для команд і запитів
       (напр. кешувати лише запити, логувати зміни лише для команд).
   ============================================================================= */

/// <summary>Команда без результату (операція запису).</summary>
public interface ICommand : IRequest { }

/// <summary>Команда з результатом (наприклад, Id створеного документа).</summary>
public interface ICommand<out TResponse> : IRequest<TResponse> { }

/// <summary>Запит на читання. Стан не змінює.</summary>
public interface IQuery<out TResponse> : IRequest<TResponse> { }
