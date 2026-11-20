# Лабораторна робота № 03 — Тришарова архітектура на базі Entity Framework Core

**Предметна область:** Система управління бібліотекою
**Мікросервіс:** «Каталог книг» (Catalog)
**База даних:** `LibraryCatalogDb` (Проєкт №2 з [Лаб. 01](../Lab01-Databases/))

---

## Структура рішення

Така сама, як у Лаб. 02; відмінність у тому, що доступ до даних будується не
вручну на ADO.NET/Dapper, а на **EF Core**: моделі, Fluent API, міграції,
LINQ to Entities.

| Проєкт | Призначення |
|---|---|
| [`src/Library.Catalog.Domain`](src/Library.Catalog.Domain/) | сутності з **бізнес-правилами в методах**, доменні винятки |
| [`src/Library.Catalog.Dal`](src/Library.Catalog.Dal/) | `DbContext`, конфігурації Fluent API, міграції, репозиторії, Unit of Work, **специфікації** |
| [`src/Library.Catalog.Bll`](src/Library.Catalog.Bll/) | сервіси, DTO, профілі AutoMapper, валідатори FluentValidation |
| [`src/Library.Catalog.Api`](src/Library.Catalog.Api/) | контролери, глобальний обробник помилок, Swagger |
| [`tests/Library.Catalog.Tests`](tests/Library.Catalog.Tests/) | **76 тестів**: інваріанти сутностей, репозиторії, специфікації, валідатори, мапінг |
| [`tests/api-tests.py`](tests/api-tests.py) | **73 перевірки** всіх критеріїв приймання через HTTP |

---

## Запуск

### 1. Підняти SQL Server

```powershell
docker run -d --name netlabs-sql -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=LabsDev!Pass2026" -e "MSSQL_PID=Developer" -p 1433:1433 -v netlabs-sqldata:/var/opt/mssql --restart unless-stopped mcr.microsoft.com/mssql/server:2022-latest
```

### 2. Застосувати міграції

```powershell
dotnet tool install --global dotnet-ef --version 9.0.11
```

```powershell
dotnet ef database update --project src\Library.Catalog.Dal --startup-project src\Library.Catalog.Api
```

> **Версія інструмента має відповідати EF Core.** Проєкт використовує EF Core
> **9.0.11** і націлений на `net9.0`, тому потрібен `dotnet-ef` саме **9.x**:
> `dotnet-ef` 11.x вимагає рантайм .NET 10+ і падає з
> `Could not load file or assembly 'System.Runtime, Version=10.0.0.0'`.

### 3. Запустити API

```powershell
dotnet run --project src\Library.Catalog.Api --urls http://localhost:5290
```

Swagger UI: **http://localhost:5290/swagger** (корінь `/` робить редирект туди).

При старті застосунок сам застосовує міграції (`Database:MigrateOnStartup`)
і виконує **ідемпотентний** сідінг (`Database:SeedOnStartup`), тож кроку 2
можна й не робити окремо.

### Перевизначення рядка підключення

```powershell
$env:ConnectionStrings__CatalogDb = "Server=localhost,1433;Database=LibraryCatalogDb;User Id=sa;Password=LabsDev!Pass2026;TrustServerCertificate=True;Encrypt=False"
```

---

## Частина 1 — EF Core (Code-First) і Fluent API

### Моделі з бізнес-правилами в методах

Усі властивості сутностей мають **`private set`**: стан не можна зіпсувати
«збоку». Правила перевіряють конструктори й методи — так само, як
`CatalogItem` в eShop (`RemoveStock` / `AddStock`).

```csharp
public void RemoveStock(int quantity)
{
    if (quantity <= 0)
        throw new DomainException("Quantity must be positive");
    if (AvailableCopies < quantity)
        throw new DomainException(
            $"Only {AvailableCopies} copies of '{Title}' are available, {quantity} requested");

    AvailableCopies -= quantity;
    UpdatedAt = DateTime.UtcNow;
}
```

| Сутність | Інваріанти, які вона боронить сама |
|---|---|
| `Book` | тариф > 0; достатній залишок; інвентарний номер унікальний у межах книги; головний жанр лише один; жанр не призначається двічі |
| `Author` | автора з книгами видалити не можна |
| `BookCopy` | стан лише з переліку `New/Good/Worn/Damaged/Lost` |
| `BookDetail` | опис не порожній; кількість сторінок > 0 |

### Fluent API

Окремий клас `IEntityTypeConfiguration<T>` на кожну сутність; усі підключаються
**централізовано** одним рядком:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
    => modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
```

### Зв'язки

| Тип | Зв'язок | Реалізація |
|---|---|---|
| **1:1** | `Book` ↔ `BookDetail` | `HasOne(b => b.Detail).WithOne().HasForeignKey<BookDetail>(d => d.BookId)` — PK залежної сутності є водночас FK |
| **1:N** | `Author` → `Book` | `HasOne(b => b.Author).WithMany(a => a.Books)`, `DeleteBehavior.Restrict` |
| **1:N** | `Book` → `BookCopy` | `HasMany(b => b.Copies).WithOne()`, `DeleteBehavior.Cascade` |
| **M:N** | `Book` ↔ `Genre` | явна проміжна сутність `BookGenre` зі складеним ключем `(BookId, GenreId)` |

**Чому M:N описано явною сутністю.** Проміжна таблиця має **власні поля**
`AddedAt`, `AddedBy`, `IsPrimary`. Автоматичний M:N
(`HasMany().WithMany()`) створив би «чисту» таблицю лише з двома зовнішніми
ключами і зберегти ці поля не дозволив би. Саме завдяки `AddedAt` працює
LINQ-запит «нові книги жанру за період».

### Обмеження й індекси

Міграція створює **6 CHECK-обмежень** (`CK_Books_DailyFee`,
`CK_Books_AvailableCopies`, `CK_Books_PublishedYear`, `CK_Authors_BirthYear`,
`CK_BookCopies_Condition`, `CK_BookDetails_PageCount`), унікальні індекси
`UQ_Books_Isbn`, `UQ_Genres_Name`, `UQ_Genres_Slug`,
`UQ_BookCopies_InventoryNumber` та пошукові індекси — кожен із коментарем,
під який запит його створено (див. [`Configurations/`](src/Library.Catalog.Dal/Configurations/)).

`RowVersion` (`IsRowVersion()`) у `Book` і `Author` дає **оптимістичну
конкурентність**: конфлікт виявляється під час запису й повертається як **409**.

### Міграції

```powershell
dotnet ef migrations add InitialCreate --project src\Library.Catalog.Dal --startup-project src\Library.Catalog.Api
```

```powershell
dotnet ef migrations script --project src\Library.Catalog.Dal --startup-project src\Library.Catalog.Api --output Docs\InitialCreate.sql --idempotent
```

Згенерований скрипт: [`Docs/InitialCreate.sql`](Docs/InitialCreate.sql).
Папка міграцій: [`src/Library.Catalog.Dal/Migrations/`](src/Library.Catalog.Dal/Migrations/).

---

## Частина 2 — Репозиторії, Unit of Work, три способи завантаження

### Async Generic Repository

`GetByIdAsync`, `GetAllAsync`, `AddAsync`, `Update`, `Delete`,
`SaveChangesAsync` + `ListAsync`/`CountAsync` для специфікацій.
`Update` і `Delete` **синхронні**: вони лише змінюють стан у контексті й до бази
не звертаються.

### Три специфічні репозиторії

| Репозиторій | Спосіб | Метод | Запитів до БД |
|---|---|---|---|
| `BookRepository` | **Eager Loading** | `GetWithDetailsAsync` — `Include`/`ThenInclude` | **1** |
| `AuthorRepository` | **Explicit Loading** | `GetWithBooksAsync` — `Entry(...).Collection(...).LoadAsync()` | **2** |
| `BookGenreRepository` | **LINQ to Entities** | `GetRecentlyAddedAsync` — через проміжну сутність M:N і її поле `AddedAt` | 1 |

**Підтверджено логом SQL-команд EF Core** —
[`../Docs/lab03-eager-vs-explicit.log`](../Docs/lab03-eager-vs-explicit.log):

```
--- EAGER LOADING: GET /api/books/1 -> ОДИН запит із JOIN-ами -------------
[02:22:42 INF] Executed DbCommand (3ms) [Parameters=[@__id_0='1'], ...]

--- EXPLICIT LOADING: GET /api/authors/1?includeBooks=true -> ДВА запити ---
[02:22:43 INF] Executed DbCommand (2ms) [Parameters=[@__id_0='1'], ...]
[02:22:43 INF] Executed DbCommand (2ms) [Parameters=[@__p_0='1'],  ...]

ВИСНОВОК: Eager = 1 запит, Explicit = 2 запити.
```

> Explicit Loading працює лише для **відстежуваних** сутностей, тому
> `GetWithBooksAsync` свідомо не викликає `AsNoTracking()` на кореневій сутності.

### Unit of Work

`SaveChangesAsync()` викликається **ОДИН раз** наприкінці бізнес-операції:
репозиторії лише додають/змінюють/видаляють сутності в контексті, а всі зміни
потрапляють у базу однією транзакцією.

На відміну від Лаб. 02, транзакцію вручну відкривати не треба — `DbContext`
сам є одиницею роботи (як `OrderingContext` в eShop). Явна транзакція
(`BeginTransactionAsync`) лишена для рідкісного випадку, коли одна операція
робить кілька послідовних `SaveChangesAsync`.

---

## Частина 3 — DTO, AutoMapper, Web/API

### Ендпоінти

| Метод | Шлях | Призначення | Статуси |
|---|---|---|---|
| GET | `/api/books` | список із **пагінацією, фільтрацією, сортуванням** | 200, 400 |
| GET | `/api/books/{id}` | картка книги (**Eager Loading**) | 200, 404 |
| GET | `/api/books/out-of-stock` | друга специфікація | 200 |
| GET | `/api/books/by-genre/{slug}` | третя специфікація | 200 |
| POST | `/api/books` | створити книгу | **201** + `Location`, 400, 404, 409 |
| PUT | `/api/books/{id}` | змінити назву й тариф | 200, 400, 404, 409 |
| POST | `/api/books/{id}/add-stock` | оприбуткувати примірники | 200, 404 |
| POST | `/api/books/{id}/remove-stock` | списати примірники | 200, 404, **409** |
| POST | `/api/books/{id}/copies` | додати примірник (1:N) | 200, 400, 404, **409** |
| POST | `/api/books/{id}/genres` | призначити жанр (M:N) | 200, 404, **409** |
| DELETE | `/api/books/{id}` | м'яке видалення | **204**, 404 |
| GET | `/api/authors` | перелік авторів | 200 |
| GET | `/api/authors/{id}?includeBooks=` | автор (**Explicit Loading**) | 200, 404 |
| POST | `/api/authors` | створити автора | **201**, 400, 409 |
| DELETE | `/api/authors/{id}` | м'яке видалення | **204**, 404, **409** |
| GET | `/api/genres` | довідник жанрів | 200 |
| GET | `/api/genres/stats` | книг у кожному жанрі (`GROUP BY` у БД) | 200 |
| GET | `/api/genres/{name}/recent-books` | **LINQ через M:N** за полем `AddedAt` | 200, 400 |
| GET | `/health` | доступність бази | 200, 503 |

Контролери **тонкі**: не звертаються до `DbContext`, не містять бізнес-правил.

---

## Частина 4 — Сідінг, пагінація, валідація, помилки, логування, специфікації

### 4.1. Два режими ідемпотентного сідінгу

| Режим | Де | Що саме |
|---|---|---|
| **Модельний (статичний)** | `HasData` у `AuthorConfiguration`, `GenreConfiguration` | довідники з **фіксованими Id** — ними керують міграції |
| **Програмний** | [`DbInitializer.SeedAsync`](src/Library.Catalog.Dal/DbInitializer.cs) після `MigrateAsync()` | книги з навігаціями (`BookDetail`, `BookCopy`, `BookGenre`) |

Ідемпотентність: перед вставкою перевіряється наявність за бізнес-ключем (ISBN),
тому **повторний запуск дублікатів не створює**. Цілісність FK дотримана:
спочатку довідники (через міграцію), потім залежні записи.
Сідінг вимикається в production (`Database:SeedOnStartup`).

> Значення в `HasData` **статичні** (`SeedConstants.SeedDate`), а не
> `DateTime.UtcNow`: EF Core порівнює seed-дані між міграціями, і динамічне
> значення давало б нову міграцію при кожній генерації.

### 4.2. Пагінація, фільтрація, сортування — на рівні БД

**Контракт запиту:** `page` (з 1), `pageSize` (за замовчуванням 20, максимум
100), `search`, `minFee`, `maxFee`, `authorId`, `genre`, `onlyAvailable`,
`sortBy` (`title|fee|year|copies`), `sortDir` (`asc|desc`).

**Контракт відповіді:** `items`, `totalCount`, `page`, `pageSize`, `totalPages`,
`hasNext`, `hasPrevious`. Метадані дублюються в заголовках
`X-Pagination-TotalCount`, `-Page`, `-PageSize`, `-TotalPages`
(тіло — зручне клієнту, заголовки — для кешування).

Фільтрація, сортування та `Skip`/`Take` виконуються **до** матеріалізації,
тому база повертає лише потрібну сторінку (`OFFSET … FETCH`), а `totalCount`
рахує теж БД окремим `COUNT(*)` **без** пагінації.

Сортування дозволене **лише за полями з білого списку** — довільний рядок
із query відхиляється з 400.

### 4.3. Два рівні валідації

| Рівень | Чим | Що перевіряє |
|---|---|---|
| **Модельний** | `DataAnnotations` на DTO | формат: обов'язковість, довжина, діапазон |
| **Складні правила** | **FluentValidation** | регулярний вираз ISBN, взаємозалежні поля (`publisher` без `description` → 400), заборона дублікатів жанрів, білий список полів сортування |

Правила **не дублюються**: DataAnnotations відповідають за формат,
FluentValidation — за складну логіку.

> Автоматична валідація через пакет `FluentValidation.AspNetCore`
> (`AddFluentValidation`) **застаріла** й більше не підтримується. Валідатори
> зареєстровано через `FluentValidation.DependencyInjectionExtensions`
> (`AddValidatorsFromAssemblyContaining`) і викликаються **явно** в контролері:
>
> ```csharp
> var validation = await validator.ValidateAsync(input, ct);
> if (!validation.IsValid)
>     return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
> ```

### 4.4. Централізована обробка помилок (ProblemDetails)

| Виняток | HTTP |
|---|---|
| `EntityNotFoundException` | **404** |
| `ValidationException` | **400** (`ValidationProblemDetails` із розбивкою по полях) |
| `ConflictException` | **409** |
| `DomainException` (інваріант сутності) | **409** |
| `DbUpdateConcurrencyException` | **409** |
| `DbUpdateException` з порушенням UNIQUE (2601/2627) | **409** |
| решта | **500** (деталі — лише в Development) |

Порядок перевірки важливий: похідні типи стоять **перед** базовим
`DomainException`. Два останні рядки — **страховка на рівні БД**: перевірку
інваріанта робить сутність, але в умовах конкурентності двоє користувачів
можуть пройти її одночасно; тоді спрацює UNIQUE-індекс, і це теж конфлікт,
а не внутрішня помилка сервера.

Кожен виняток логується **рівно один раз** — в обробнику.

### 4.5. Логування — Serilog

Структуровані логи, sink-и Console + File (`logs/catalog-api-*.log`),
збагачення `FromLogContext` і властивістю `Service`. Рівень
`Microsoft.EntityFrameworkCore.Database.Command` піднято до `Information` —
саме це дає змогу бачити SQL-запити EF Core і перевірити Eager vs Explicit.

### 4.6. Specification pattern (Ardalis.Specification)

Складна логіка вибірок винесена зі сервісів у класи специфікацій
([`Specifications/`](src/Library.Catalog.Dal/Specifications/)):

| Специфікація | Що описує |
|---|---|
| `BooksFilterSpec` | фільтрація + сортування + `Include` + пагінація |
| `BooksByGenreSlugSpec` | топ книг жанру за slug |
| `OutOfStockBooksSpec` | книги, яких немає в наявності |

Репозиторій лише **приймає** специфікацію й застосовує її
(`Set.WithSpecification(spec)`), нічого не знаючи про критерії. Усі три
специфікації працюють через **той самий** метод `ListAsync` — у репозиторії
не змінено жодного рядка (перевірено тестами).

---

## Тестування

### Unit-тести — 76 тестів

```powershell
dotnet test tests\Library.Catalog.Tests
```

| Група | Що перевіряє |
|---|---|
| `DomainEntityTests` | інваріанти сутностей **без бази й без моків** — головна перевага реалізації правил у методах |
| `RepositoryTests` | Eager / Explicit Loading, LINQ через M:N, усі три специфікації, `CountAsync` ігнорує пагінацію, ідемпотентність сідінгу (провайдер InMemory) |
| `ValidatorTests` | формат ISBN, взаємозалежні поля, білий список сортування, межі `pageSize` |
| `MappingTests` | `AssertConfigurationIsValid()` + проєкції в DTO |
| `PagedResultTests` | `TotalPages`, `HasNext`, `HasPrevious`, обмеження `pageSize` |

### API-тести — 73 перевірки

```powershell
py -I tests\api-tests.py http://localhost:5290
```

Скрипт **самоочисний**: на старті прибирає залишки попередніх прогонів, тому
його можна запускати багато разів поспіль.

| Перевірка з методички | Блок | Результат |
|---|---|---|
| у лозі SQL eager-репозиторій робить 1 запит, explicit — 2 | 7 + окремий лог | підтверджено |
| POST із порожнім `Name` → **400** з переліком помилок по полях, а не 500 | 3 | 400 + `errors` |
| повторне створення з тим самим ISBN → **409** | 5 | 409 |
| неіснуючий Id → **404** у форматі ProblemDetails | 4 | 404 |
| `page=2&pageSize=5` → `totalCount` = **усі** записи в БД, а не на сторінці | 1 | підтверджено |
| повторний запуск застосунку не дублює seed-дані | 0 | 6 книг після багатьох рестартів |
| друга специфікація працює через той самий `WithSpecification()` без змін у репозиторії | 9 | підтверджено |

Збережений вивід: [`../Docs/lab03-api-tests.log`](../Docs/lab03-api-tests.log).

### Знімки екрана

- [`../Docs/screenshots/lab03-swagger.png`](../Docs/screenshots/lab03-swagger.png) — Swagger з ендпоінтами
- [`../Docs/screenshots/lab03-pagination.png`](../Docs/screenshots/lab03-pagination.png) — відповідь із пагінацією

---

## Критерії приймання — відповідність

| Критерій Лаб. 03 | Де виконано |
|---|---|
| сутності з ключами, зв'язками та обмеженнями (Code First + Fluent API) | [`Configurations/`](src/Library.Catalog.Dal/Configurations/) |
| конфігурації підключені централізовано в `OnModelCreating` | `ApplyConfigurationsFromAssembly` |
| **є M:N з проміжною сутністю** | `BookGenre` з полями `AddedAt`, `AddedBy`, `IsPrimary` |
| міграції, **ідемпотентний сідінг**, коректне застосування змін | [`Migrations/`](src/Library.Catalog.Dal/Migrations/), `HasData` + `DbInitializer` |
| **Async Generic Repository** | [`GenericRepository<T>`](src/Library.Catalog.Dal/Repositories/Repositories.cs) |
| **3 специфічні репозиторії** (eager, explicit, LINQ для M:N) | `BookRepository`, `AuthorRepository`, `BookGenreRepository` |
| **Unit of Work** | [`UnitOfWork`](src/Library.Catalog.Dal/UnitOfWork.cs), один `SaveChangesAsync` |
| бізнес-сервіси, DTO, AutoMapper; контролери не чіпають `DbContext` | [`Bll/`](src/Library.Catalog.Bll/), [`Controllers/`](src/Library.Catalog.Api/Controllers/) |
| асинхронні контролери, коректні коди, Swagger | усі дії з `CancellationToken`; `/swagger` |
| **пагінація, фільтрація, сортування на рівні SQL** | `BooksFilterSpec` + `CountAsync` без пагінації |
| валідація DataAnnotations + FluentValidation → `ValidationProblemDetails` | [`Validators/`](src/Library.Catalog.Bll/Validators/) |
| централізована обробка помилок (ProblemDetails) | [`DomainExceptionHandler`](src/Library.Catalog.Api/Infrastructure/DomainExceptionHandler.cs) |
| логування Serilog зі структурованими властивостями | [`appsettings.json`](src/Library.Catalog.Api/appsettings.json) |
| **Specification pattern** | [`Specifications/`](src/Library.Catalog.Dal/Specifications/) |

---

## Попередня робота

**Лаб. 02** — та сама тришарова архітектура, але на **ADO.NET та Dapper** над
базою Проєкту №1 (`LibraryLendingDb`), з Unit of Work на явних транзакціях,
збережуваними процедурами та .NET Aspire →
[`../Lab02-AdoNet-Dapper/`](../Lab02-AdoNet-Dapper/)
