# Лабораторна робота № 04 — Clean Architecture, CQRS, Mediator і MongoDB

**Предметна область:** Система управління бібліотекою
**Мікросервіс:** «Відгуки та обговорення» (Reviews)
**База даних:** `LibraryReviewsDb` (Проєкт №3 з [Лаб. 01](../Lab01-Databases/))

---

## Структура рішення — чотири шари

```
Library.Reviews.Domain/          ← НЕ залежить ні від кого
├── Common/                      BaseEntity, ValueObject
├── Entities/                    Review (+ Comment), Discussion (+ DiscussionMessage)
├── ValueObjects/                Author, Rating, BookRef
├── Exceptions/                  DomainException, NotFoundException, ConflictException
└── Interfaces/                  IReviewRepository, IDiscussionRepository

Library.Reviews.Application/     ← залежить лише від Domain
├── Common/
│   ├── Behaviors/               Validation, Logging, Performance, ExceptionHandling
│   ├── Interfaces/              IReviewQueries + DTO читання
│   ├── Messaging/               ICommand, ICommand<T>, IQuery<T>
│   └── Models/                  PagedResult<T>, CursorPage<T>
├── Reviews/Commands/            Create, Update, Delete, AddComment, RefreshBookTitle
├── Reviews/Queries/             GetById, GetList, ByCursor, BookRatings, Search
├── Discussions/                 команди, запити та DTO обговорень
└── DependencyInjection.cs       AddApplication()

Library.Reviews.Infrastructure/  ← реалізує інтерфейси внутрішніх шарів
├── Persistence/                 MongoDbContext, MongoMappings, репозиторії, запити
├── Seeding/                     ReviewSeeder, IndexCreationService
└── DependencyInjection.cs       AddInfrastructure()

Library.Reviews.WebApi/          ← контролери + DI-конфігурація
├── Controllers/                 ReviewsController, DiscussionsController
├── Infrastructure/              ApiExceptionHandler
└── Program.cs
```

### Правило залежностей

```
WebApi ──────► Application ──────► Domain
   │                                 ▲
   └──► Infrastructure ──────────────┘
         (лише для реєстрації DI під час старту)
```

Внутрішні шари **не знають** про зовнішні. `Domain` не посилається на жоден
інший проєкт рішення й навіть на `MongoDB.Driver` — лише на `MongoDB.Bson`
(пакет **типів**, потрібен `ObjectId`).

Це перевіряється **автоматичним тестом**, а не на око —
[`ArchitectureTests`](tests/Library.Reviews.Tests/ArchitectureAndApplicationTests.cs):

| Тест | Що гарантує |
|---|---|
| `Domain_DoesNotReferenceOtherLayers` | Domain не бачить Application / Infrastructure / WebApi |
| `Domain_DoesNotReferenceMongoDriver` | Domain знає `MongoDB.Bson`, але не драйвер |
| `Domain_HasNoFrameworkDependencies` | у Domain немає MediatR, FluentValidation, ASP.NET Core |
| `Application_DoesNotReferenceInfrastructureOrWebApi` | Application залежить лише від Domain |
| `Application_DoesNotReferenceMongoDriver` | жоден обробник не бачить `IMongoCollection` |
| `EveryRequest_HasExactlyOneHandler` | кожна команда й запит мають рівно один обробник |
| `AllPipelineBehaviors_Exist` | усі чотири behaviors на місці |

---

## Запуск

```powershell
docker start netlabs-mongo
```

```powershell
dotnet run --project src\Library.Reviews.WebApi --urls http://localhost:5590
```

Swagger UI: **http://localhost:5590/swagger**

Під час старту сервіс сам створює індекси й виконує **ідемпотентний** сідінг.
Якщо база вже наповнена скриптами Лаб. 01, сідінг пропускається — сервіс
працює з тими самими документами.

### Конфігурація

```json
"MongoDb": {
  "ConnectionString": "mongodb://localhost:27017/?maxPoolSize=50&serverSelectionTimeoutMS=5000",
  "DatabaseName": "LibraryReviewsDb"
}
```

Параметри пулу з'єднань і таймаутів задаються **в рядку підключення**.
Перевизначення змінною оточення: `MongoDb__ConnectionString`.

---

## Частина 2 — Доменний шар

### Сутності

| Сутність | Роль | Особливості MongoDB |
|---|---|---|
| `Review` | корінь агрегату «Відгук» | вбудований масив `comments`, вбудований `author`, **посилання** `discussionId`, гнучкі поля `photos`/`tags` |
| `Discussion` | корінь агрегату «Обговорення» | вбудований масив `messages`; винесена в **окрему колекцію**, бо зростає необмежено |

`BaseEntity` дає `Id` (`ObjectId`), `CreatedAt`, `UpdatedAt` і **`Version`**
для оптимістичної конкурентності.

Інкапсуляція: усі властивості мають `private set`, колекції віддаються як
`IReadOnlyCollection`, стан змінюють лише методи — і вони ж боронять інваріанти:

| Інваріант | Де |
|---|---|
| оцінка лише 1–5 | `Rating` (Value Object) |
| текст відгуку ≤ 2000 символів | `Review` |
| не більше 50 вбудованих коментарів (далі — обговорення) | `Review.AddComment` |
| фото й теги не дублюються | `Review.AddPhoto`, `AddTag` |
| у закрите обговорення писати не можна | `Discussion.Post` |
| `replyTo` має вказувати на наявне повідомлення | `Discussion.Post` |

### Value Objects

Три незмінні Value Objects із порівнянням **за значенням**
(базовий клас — як `ValueObject` в eShop):

| VO | Компоненти | Де використовується |
|---|---|---|
| `Author` | `ReaderId?`, `DisplayName` | автор відгуку, коментаря, повідомлення |
| `Rating` | `Value` (1–5) | оцінка у відгуку |
| `BookRef` | `BookId`, `BookTitle` | посилання на Каталог + денормалізована назва |

`BookRef` — це пряме втілення **таблиці політики дублювання з Лаб. 01**:
контекст Відгуків зберігає лише ідентифікатор книги (логічне посилання, **без
FOREIGN KEY**) і копію назви для читання.

---

## Частина 3 — CQRS з MediatR

### Команди (запис)

| Команда | Результат | Валідація |
|---|---|---|
| `CreateReviewCommand` | `string` (ObjectId) | діапазони + **асинхронна** перевірка «один відгук від читача на книгу» |
| `UpdateReviewCommand` | — | формат ObjectId, діапазони, перевірка версії |
| `DeleteReviewCommand` | — | формат ObjectId |
| `AddCommentCommand` | — | непорожній текст ≤ 1000 |
| `RefreshBookTitleCommand` | `long` | оновлення денормалізованої назви |
| `CreateDiscussionCommand` | `string` | тема ≤ 300 |
| `PostDiscussionMessageCommand` | — | текст, `replyTo` |
| `CloseDiscussionCommand` | — | формат ObjectId |
| `AttachDiscussionCommand` | — | обидва ObjectId |

### Запити (читання)

| Запит | Що робить MongoDB |
|---|---|
| `GetReviewByIdQuery` | `find` за `_id` |
| `GetReviewsListQuery` | `$match` + `skip`/`limit` + `countDocuments` |
| `GetReviewsByCursorQuery` | **курсорна пагінація** через `_id < lastId` |
| `GetBookRatingsQuery` | **aggregation pipeline** `$group` (середня оцінка, min/max, сума коментарів) |
| `SearchReviewsQuery` | повнотекстовий пошук через **text-індекс** |
| `GetDiscussionByIdQuery`, `GetDiscussionsByBookQuery` | `find` + сортування |

**Обробник запиту не звертається до MongoDB напряму.** Він викликає
`IReviewQueries` (інтерфейс в Application), а реалізація — в Infrastructure.
Так само в eShop запити оголошено інтерфейсом `IOrderQueries`.

---

## Частина 4 — Pipeline Behaviors

Порядок реєстрації визначає порядок виконання:

```
запит → Logging → ExceptionHandling → Validation → Performance → обробник
```

| Behavior | Призначення |
|---|---|
| `LoggingBehavior` | «Handling …» на вході, «Handled …» на виході |
| `ExceptionHandlingBehavior` | логує невиловлені винятки з контекстом операції (не поглинає) |
| `ValidationBehavior` | запускає всі валідатори FluentValidation; при помилках кидає `ValidationException` — **обробник не викликається** |
| `PerformanceBehavior` | попереджає, якщо операція триває понад 500 мс |

> **Важлива деталь реалізації.** Кожен валідатор отримує **власний**
> `ValidationContext`. Якщо передати один спільний (як у типових прикладах),
> FluentValidation накопичує в ньому помилки, і результат другого валідатора
> міститиме ще й помилки першого — на виході вони подвоюються. Цей дефект
> упіймав тест `Handle_CollectsFailuresFromAllValidators`.

### Доказ, що валідація спрацьовує ДО обробника

[`../Docs/lab04-pipeline-behaviors.log`](../Docs/lab04-pipeline-behaviors.log):

```
--- 1. КОРЕКТНА команда: у лозі є і "Handling", і "Handled" ---------------
[INF] Handling CreateReviewCommand
[INF] Handled CreateReviewCommand

--- 2. НЕКОРЕКТНА команда: є "Handling", але "Handled" ВІДСУТНІЙ ----------
[INF] Handling CreateReviewCommand
[WRN] Validation failed for CreateReviewCommand: Rating: 'Rating' має бути між 1 та 5 (включно). Ви ввели 10.
```

---

## Частина 5 — Інфраструктура MongoDB

### BSON-серіалізація

Мапінг налаштовано **в Infrastructure через `BsonClassMap`**, а не атрибутами
на доменних класах: атрибути прив'язали б Domain до деталей зберігання.

| Прийом | Навіщо |
|---|---|
| `CamelCaseElementNameConvention` | імена полів як у схемі Лаб. 01 |
| `IgnoreExtraElementsConvention` | гнучка схема: у документі можуть бути поля, яких немає в класі |
| `DateTimeSerializer(DateTimeKind.Utc)` | час завжди в UTC |
| **`RatingSerializer`** | `Rating` зберігається **числом**, а не вкладеним об'єктом — сумісність зі схемою Лаб. 01 (`bsonType: "int"`) |
| **`AuthorSerializer`** | власний серіалізатор VO, толерантний до анонімних авторів без `readerId` |
| `MapField("_comments")` тощо | приватні поля-колекції; публічні властивості віддають `IReadOnlyCollection`, писати в них драйвер не може |
| окрема карта для `BaseEntity` | `MapIdProperty` вимагає член, оголошений **саме в цьому класі** |

> `bookId` і `bookTitle` зберігаються **окремими полями верхнього рівня**, бо
> саме так їх описано в Лаб. 01. У домені вони все одно подані повноцінним
> Value Object `BookRef` — деталь зберігання не протікає в доменну модель.

### Оптимістична конкурентність

```csharp
var result = await _reviews.ReplaceOneAsync(
    r => r.Id == review.Id && r.Version == expectedVersion, review, ct);

if (result.MatchedCount == 0)
    throw new ConflictException($"Review {review.Id} was modified by another user");
```

Назовні це доступно через заголовки **`ETag` / `If-Match`**: `GET` повертає
версію в `ETag`, `PUT` передає її в `If-Match`. Застаріла версія → **409**.

### Індекси (створюються автоматично при старті)

| Індекс | Під який запит | Тип |
|---|---|---|
| `ix_reviews_book_created` `{bookId:1, createdAt:-1}` | «останні відгуки про книгу» | складений |
| `ix_reviews_book_rating` `{bookId:1, rating:-1}` | агрегація рейтингів | складений |
| `ix_reviews_fulltext` `{text, bookTitle}` | повнотекстовий пошук | text, `default_language: none` |
| `ux_reviews_book_reader` `{bookId:1, author.readerId:1}` | один відгук від читача на книгу | **unique + partial** |
| `ix_discussions_book_open` | «відкриті обговорення книги» | складений |
| `ix_discussions_activity` | «останні активні обговорення» | sparse |

> Унікальний індекс — **partial**, а не `sparse`. Для **складеного** індексу
> `sparse` виключає документ лише тоді, коли відсутні **всі** його поля.
> Оскільки `bookId` є завжди, анонімні відгуки (без `readerId`) усе одно
> потрапили б в індекс зі значенням `null` — і двох анонімних відгуків на одну
> книгу створити вже не вийшло б. Частковий фільтр
> `{ "author.readerId": { $exists: true } }` індексує лише ті документи, де
> читач справді вказаний.

Якщо індекс із таким іменем існує з **іншими** опціями, сервіс сам його
перестворює (обробка кодів 85/86), тому зміна визначення індексу в коді не
вимагає ручного втручання в базу.

---

## Частина 7 — Обробка помилок

| Виняток | HTTP |
|---|---|
| `NotFoundException` | **404** |
| `ValidationException` (FluentValidation) | **400** `ValidationProblemDetails` із розбивкою по полях |
| `ConflictException` (у т. ч. конфлікт версій) | **409** |
| `DomainException` | **400** |
| `MongoWriteException` з `DuplicateKey` | **409** |
| інші помилки запису MongoDB | **400** |
| `MongoConnectionException`, `TimeoutException` | **503** |
| `FormatException` (некоректний ObjectId) | **400** |
| решта | **500** (деталі лише в Development) |

Порядок перевірки важливий: похідні типи стоять **перед** базовим
`DomainException`.

---

## Ендпоінти

| Метод | Шлях | Призначення |
|---|---|---|
| GET | `/api/reviews` | список із пагінацією та фільтрами |
| GET | `/api/reviews/by-cursor` | курсорна пагінація |
| GET | `/api/reviews/ratings` | рейтинги книг (aggregation) |
| GET | `/api/reviews/search?term=` | повнотекстовий пошук |
| GET | `/api/reviews/{id}` | відгук + `ETag` |
| POST | `/api/reviews` | створити (**201** + `Location`) |
| PUT | `/api/reviews/{id}` | оновити (`If-Match`) |
| DELETE | `/api/reviews/{id}` | видалити |
| POST | `/api/reviews/{id}/comments` | додати вбудований коментар |
| PUT | `/api/reviews/{id}/discussion/{discussionId}` | прикріпити обговорення |
| POST | `/api/reviews/refresh-book-title` | оновити денормалізовану назву |
| GET | `/api/discussions/{id}` | обговорення + `ETag` |
| GET | `/api/discussions/by-book/{bookId}` | обговорення книги |
| POST | `/api/discussions` | створити |
| POST | `/api/discussions/{id}/messages` | додати повідомлення |
| POST | `/api/discussions/{id}/close` | закрити |
| GET | `/health` | ping до MongoDB |

---

## Тестування

### Unit-тести — 72 тести

```powershell
dotnet test tests\Library.Reviews.Tests
```

| Група | Що перевіряє |
|---|---|
| `ArchitectureTests` | правило залежностей Clean Architecture (7 тестів) |
| `ReviewEntityTests`, `DiscussionEntityTests` | інваріанти сутностей **без бази й без моків** |
| `ValueObjectTests` | порівняння за значенням, незмінність, валідація |
| `ValidationBehaviorTests` | обробник **не викликається** при помилці валідації |
| `HandlerTests` | обробники CQRS із замоканими доменними інтерфейсами |
| `ValidatorTests` | діапазони, формат ObjectId, асинхронне правило «один відгук» |

### API-тести — 66 перевірок

```powershell
py -I tests\api-tests.py http://localhost:5590
```

| Критерій із методички | Блок | Результат |
|---|---|---|
| `Reviews.Domain` не має посилань на інші проєкти | unit `ArchitectureTests` | ✅ |
| жоден обробник не звертається до `IMongoCollection` | unit `ArchitectureTests` | ✅ |
| команда з `Rating = 10` → **400** до виклику обробника | 2 + лог | ✅ |
| `db.reviews.find()` без витоку внутрішніх полів .NET | 1 | ✅ |
| `db.reviews.getIndexes()` — створені індекси | старт сервісу | ✅ |
| повторний відгук того самого читача відхиляється | 5 | ✅ |
| повторний запуск застосунку не дублює seed-дані | 0 | ✅ |
| конфлікт версій → **409** | 6 | ✅ |
| неіснуючий Id → **404** | 3 | ✅ |

Збережений вивід: [`../Docs/lab04-api-tests.log`](../Docs/lab04-api-tests.log).

### Перевірка структури документа в mongosh

```powershell
docker exec netlabs-mongo mongosh "mongodb://localhost:27017/LibraryReviewsDb" --quiet --eval "db.reviews.findOne()"
```

```powershell
docker exec netlabs-mongo mongosh "mongodb://localhost:27017/LibraryReviewsDb" --quiet --eval "db.reviews.getIndexes()"
```

---

## Зв'язок із попередніми роботами

| Робота | Що взято |
|---|---|
| **Лаб. 01** | структура документів `reviews` і `discussions`, індекси, політика дублювання |
| **Лаб. 02–03** | ті самі принципи ProblemDetails, валідації та Swagger, але архітектура інша: тут **чотири** шари й Domain не залежить ні від API, ні від бази |

Далі — **Лаб. 05**: цей сервіс разом із «Видачею» (Лаб. 02) і «Каталогом»
(Лаб. 03) інтегрується в єдину платформу .NET Aspire →
[`../Lab05-Aspire-Platform/`](../Lab05-Aspire-Platform/)
