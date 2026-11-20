# Лабораторна робота № 02 — Тришарова архітектура на базі ADO.NET та Dapper

**Предметна область:** Система управління бібліотекою
**Мікросервіс:** «Видача книг» (Lending) — вертикаль від бази даних до API
**База даних:** `LibraryLendingDb` (Проєкт №1 з [Лаб. 01](../Lab01-Databases/))

---

## Структура рішення

| Проєкт | Призначення |
|---|---|
| [`src/Library.Lending.Domain`](src/Library.Lending.Domain/) | моделі, доменні винятки, константи (статуси, коди помилок SQL) — спільні для DAL і BLL |
| [`src/Library.Lending.Dal`](src/Library.Lending.Dal/) | інтерфейси репозиторіїв (включно з **Generic Repository**), реалізації на **ADO.NET** і **Dapper**, **Unit of Work** |
| [`src/Library.Lending.Bll`](src/Library.Lending.Bll/) | сервіси, інтерфейси сервісів, DTO, профілі AutoMapper |
| [`src/Library.Lending.Api`](src/Library.Lending.Api/) | Web/API — контролери, глобальний обробник помилок, налаштування хосту |
| [`src/Library.Lending.AppHost`](src/Library.Lending.AppHost/) | **.NET Aspire AppHost** — запускає API разом із базою однією командою |
| [`tests/Library.Lending.Tests`](tests/Library.Lending.Tests/) | unit-тести BLL (xUnit + Moq), валідація конфігурації AutoMapper |
| [`tests/api-tests.py`](tests/api-tests.py) | автоматична перевірка **усіх критеріїв приймання** через HTTP |

### Напрямок залежностей

```
Library.Lending.Api  ──►  Library.Lending.Bll  ──►  Library.Lending.Dal
         │                        │                        │
         └────────────────────────┴────────────────────────►┘
                         Library.Lending.Domain
```

Залежності спрямовані **в один бік**: `Api → Bll → Dal`; `Domain` підключають
і `Dal`, і `Bll`. **Контролер ніколи не звертається до репозиторію напряму** —
лише через сервіс. Жодного SQL-запиту поза DAL, жодного бізнес-правила в
контролерах.

---

## Запуск

### Варіант A — через .NET Aspire (одна команда піднімає API і базу)

```powershell
dotnet run --project src/Library.Lending.AppHost
```

У консолі з'явиться посилання на **Aspire Dashboard**. Там видно обидва ресурси
(`sql` і `lending-api`) у статусі **Running** та рядок підключення до бази.
URL API беріть із Dashboard.

AppHost має **два режими**, перемикач — `Database:Mode` у
[`src/Library.Lending.AppHost/appsettings.json`](src/Library.Lending.AppHost/appsettings.json):

| Режим | Що робить |
|---|---|
| `Container` *(за замовчуванням)* | Aspire сам піднімає контейнер SQL Server 2022 на порту **14333**, створює базу `LibraryLendingDb` і застосовує до неї `schema.sql` + `procedures.sql` + `seed.sql` з Лаб. 01 |
| `Existing` | використовується вже запущений інстанс (контейнер `netlabs-sql` із Лаб. 01); Aspire лише передає рядок підключення в API |

Ім'я ресурсу бази — `LendingDb` — збігається з іменем рядка підключення, який
читає `Library.Lending.Api`, тому Aspire передає його автоматично й **код API
змінювати не потрібно**.

### Варіант B — API окремо (без Aspire)

Спочатку підніміть базу з Лаб. 01:

```powershell
docker run -d --name netlabs-sql -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=LabsDev!Pass2026" -e "MSSQL_PID=Developer" -p 1433:1433 -v netlabs-sqldata:/var/opt/mssql --restart unless-stopped mcr.microsoft.com/mssql/server:2022-latest
```

Застосуйте скрипти (деталі — у [README Лаб. 01](../Lab01-Databases/README.md)):

```powershell
docker cp "..\Lab01-Databases\Project1-SqlServer-AdoNet\." netlabs-sql:/tmp/lib-p1/
```

```powershell
docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "LabsDev!Pass2026" -C -I -f 65001 -i /tmp/lib-p1/schema.sql -i /tmp/lib-p1/procedures.sql -i /tmp/lib-p1/seed.sql
```

Запустіть API:

```powershell
dotnet run --project src/Library.Lending.Api --urls http://localhost:5190
```

Swagger UI: **http://localhost:5190/swagger** (корінь `/` робить редирект туди).

### Перевизначення рядка підключення змінною оточення

```powershell
$env:ConnectionStrings__LendingDb = "Server=localhost,1433;Database=LibraryLendingDb;User Id=sa;Password=LabsDev!Pass2026;TrustServerCertificate=True;Encrypt=False"
```

---

## Крок B — Шар доступу до даних (DAL)

### Реалізації репозиторіїв

| Репозиторій | Технологія | Успадковує Generic Repository | Що демонструє |
|---|---|---|---|
[`ReaderRepository`](src/Library.Lending.Dal/Repositories/ReaderRepository.cs) | **чистий ADO.NET** | **НІ — свідомо** | `SqlConnection` / `SqlCommand` / `SqlParameter` / `SqlDataReader`, ручний мапінг рядків, явні `SqlDbType` |
| [`LoanRepository`](src/Library.Lending.Dal/Repositories/LoanRepository.cs) | ADO.NET + **Dapper** | так | **multi-mapping** (видача + деталі + позиції одним запитом), виклики збережуваних процедур, OUTPUT-параметри |
| [`BookRepository`](src/Library.Lending.Dal/Repositories/BookRepository.cs) | ADO.NET + **Dapper** | так | процедура `usp_DecreaseBookCopies`, `MERGE` для синхронізації копії довідника |
| [`FinePaymentRepository`](src/Library.Lending.Dal/Repositories/FinePaymentRepository.cs) | ADO.NET + **Dapper** | так | агрегатний запит (`SUM` рахує БД), пошук за токеном ідемпотентності |

**Чому `ReaderRepository` НЕ успадковує Generic Repository:** його сенс — показати,
як та сама робота виконується «руками», без сторонніх бібліотек, щоб було з чим
порівняти Dapper. Узагальнений CRUD написаний один раз у
[`BaseDapperRepository<T>`](src/Library.Lending.Dal/Infrastructure/BaseDapperRepository.cs),
а конкретні Dapper-репозиторії додають лише свої специфічні запити.

### Unit of Work і транзакції

[`UnitOfWork`](src/Library.Lending.Dal/Infrastructure/UnitOfWork.cs) відкриває
**одне з'єднання** на всю бізнес-операцію й координує всі репозиторії в **одній
транзакції**.

**Ключова деталь реалізації.** Репозиторії отримують у конструкторі не *значення*
транзакції, а **делегат** `() => _transaction`:

```csharp
Readers = new ReaderRepository(_connection, () => _transaction);
Loans   = new LoanRepository(_connection, () => _transaction);
```

Якби передавалося значення, репозиторії назавжди отримали б `null` (на момент
їхнього створення транзакції ще не існує) і кожен працював би **поза**
транзакцією — відкат не охопив би їхні зміни.

Реєстрація — **Scoped**: один екземпляр і одне з'єднання на HTTP-запит:

```csharp
builder.Services.AddScoped<IUnitOfWork>(_ => new UnitOfWork(connectionString));
```

### Рівень ізоляції та компроміс deadlocks vs. consistency

Усі бізнес-транзакції працюють на **`READ COMMITTED`** (значення за замовчуванням
SQL Server). Вищий рівень (`REPEATABLE READ`, `SERIALIZABLE`) прибрав би фантомні
читання, але різко збільшив би кількість блокувань і ризик взаємних блокувань на
`dbo.Books` — найгарячішій таблиці системи. Там, де потрібна стабільність рядка в
межах транзакції, це досягається **точковою** підказкою `WITH (UPDLOCK)`
у збережуваних процедурах, а не підняттям рівня ізоляції для всієї транзакції.

Таймаут команд — за замовчуванням SqlClient (30 с); з'єднання повертаються в
**пул** через `await using` / `DisposeAsync`.

### Захист від SQL-ін'єкцій

Усі значення передаються **виключно як параметри**; конкатенації рядків із
вхідними даними немає ніде.

```csharp
// НЕБЕЗПЕЧНО (так у проєкті НЕ зроблено):
"SELECT * FROM dbo.Loans WHERE Id = " + id

// Правильно — ADO.NET:
command.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;

// Правильно — Dapper:
Connection.QueryAsync<Loan>("... WHERE Id = @Id", new { Id = id })
```

Ім'я таблиці в `BaseDapperRepository<T>` — **константа з коду** конкретного
репозиторію, а не вхідні дані користувача, тому його підстановка в текст запиту
безпечна.

Перевірка: у seed-даних є читач **`Liam O'Brien`** — апостроф не ламає жодного
запиту; а спроба ін'єкції `GET /api/books?title=' OR 1=1 --` повертає **порожній
список**, а не всі книги (тест 8 в `api-tests.py`).

### Перетворення помилок БД на доменні винятки

[`SqlExceptionTranslator`](src/Library.Lending.Dal/Infrastructure/SqlExceptionTranslator.cs)
перехоплює `SqlException` і перетворює **відомі** номери на доменні винятки:

| Код SQL | Джерело | Доменний виняток | HTTP |
|---|---|---|---|
| **50001** | `THROW` у процедурах Лаб. 01 | `NotFoundException` | **404** |
| **50002** | недопустимий перехід статусу | `BusinessConflictException` | **409** |
| **50003** | недостатньо примірників | `BusinessConflictException` | **409** |
| **50004** | порушення бізнес-правила | `BusinessConflictException` | **409** |
| 2601 / 2627 | порушення UNIQUE | `BusinessConflictException` | **409** |
| 547 | порушення FK / CHECK | `BusinessConflictException` | **409** |
| решта | обрив з'єднання, таймаут, deadlock | **не перетворюється** | **500** |

Останній рядок важливий: інфраструктурні збої — це **не** порушення бізнес-правил,
тому вони підіймаються як є й стають 500, а не маскуються під 409.
«Сирі» повідомлення SQL Server за межі DAL не передаються.

---

## Крок C — Бізнес-логіка (BLL)

### Головний транзакційний сценарій

[`LoanService.ConfirmAndIssueAsync`](src/Library.Lending.Bll/Services/LoanService.cs)
— місце, де зустрічаються Unit of Work, **два репозиторії** та збережувані
процедури з Лаб. 01:

```
BeginTransaction(ReadCommitted)
  ├─ Loans.GetWithItemsAsync      (Dapper multi-mapping)
  ├─ Loans.ConfirmCopiesAsync     -> usp_ConfirmLoanCopies  (перехід перевіряє процедура)
  ├─ Loans.IssueAsync             -> usp_IssueLoan          (зменшує залишок примірників)
  └─ Commit            АБО        Rollback при будь-якій помилці
```

Якщо на другому кроці бракує примірників, процедура кидає **50003** →
`BusinessConflictException` → `RollbackAsync` → статус видачі **повертається
назад**. Або все, або нічого.

### Два рівні валідації

| Рівень | Де | Чим | Приклад |
|---|---|---|---|
| **Модельна** | API, до виклику сервісу | `DataAnnotations` на DTO | `[Range(1, 100)] Units`, `[MinLength(1)] Items`, `[EmailAddress] Email` |
| **Бізнес-валідація** | BLL, потрібен стан із БД | код сервісу + доменні винятки | читач існує й не заблокований, примірників достатньо, email унікальний |

### Ідемпотентність ключових операцій

Оплата пені приймає `idempotencyKey`. Повторна відправка **того самого** запиту
повертає **наявний** платіж, а другий не створюється
(`UQ_FinePayments_IdempotencyKey` у БД + перевірка в сервісі). Шлях
ідемпотентності спрацьовує **раніше** за бізнес-перевірку «вже оплачено».

### Конкурентність

- **Оптимістична** (`ROWVERSION` у `Readers`, `Loans`, `Books`): конкурентні
  записи одного рядка тут рідкі, тому дешевше *виявити* конфлікт під час запису,
  ніж постійно блокувати.
- **Песимістична** (`WITH (UPDLOCK)` у процедурах): застосована точково там, де
  конфлікт потрібно *запобігти* — два паралельні підтвердження видачі не повинні
  обидва зменшити залишок примірників.

### AutoMapper

Профілі зберігаються в BLL централізовано
([`LendingProfile`](src/Library.Lending.Bll/Mapping/LendingProfile.cs)), а
конфігурація перевіряється тестом `AssertConfigurationIsValid()`.

> **Ліцензія.** Починаючи з версії 15.0 AutoMapper розповсюджується за
> комерційною ліцензією з безкоштовною **Community**-редакцією (навчальне та
> некомерційне використання, компанії з доходом до 5 млн $ на рік). Ключ
> реєструється на [automapper.io](https://automapper.io/) і підхоплюється з
> конфігурації (`AutoMapper:LicenseKey`); **без нього застосунок працює**, але
> пише в лог повідомлення про відсутню ліцензію.

---

## Крок D — Web/API

### Ендпоінти

| Метод | Шлях | Призначення | Статуси |
|---|---|---|---|
| GET | `/api/loans/{id}` | видача з деталями та позиціями | 200, 404 |
| GET | `/api/loans/overdue` | прострочені видачі | 200 |
| GET | `/api/loans/{id}/history` | журнал переходів статусів | 200, 404 |
| POST | `/api/loans` | оформити видачу | **201** + `Location`, 400, 404, 409 |
| POST | `/api/loans/{id}/confirm` | підтвердити примірники | 200, 404, **409** |
| POST | `/api/loans/{id}/issue` | **підтвердити + видати (транзакція)** | 200, 404, **409** |
| POST | `/api/loans/{id}/return` | прийняти повернення, нарахувати пеню | 200, 404, **409** |
| POST | `/api/loans/{id}/cancel` | скасувати (лише до видачі) | 200, 404, **409** |
| GET | `/api/loans/{id}/fine-payments` | оплати пені | 200, 404 |
| POST | `/api/loans/{id}/fine-payments` | оплатити пеню (ідемпотентно) | **201**, 400, 404, 409 |
| GET | `/api/readers` | перелік читачів | 200 |
| GET | `/api/readers/{id}` | читач | 200, 404 |
| GET | `/api/readers/{id}/loans?status=` | видачі читача | 200, 400, 404 |
| GET | `/api/readers/{id}/loan-summary` | звіт (процедура + view) | 200, 404 |
| POST | `/api/readers` | зареєструвати читача | **201**, 400, 409 |
| PUT | `/api/readers/{id}/blocked?value=` | заблокувати / розблокувати | **204**, 404 |
| DELETE | `/api/readers/{id}` | м'яке видалення | **204**, 404 |
| GET | `/api/books?title=` | пошук у локальній копії довідника | 200 |
| GET | `/api/books/{id}` | книга | 200, 404 |
| PUT | `/api/books/sync` | синхронізація копії з Каталогу | 200, 400 |
| GET | `/health` | перевірка доступності бази | 200 |

### Відповідність результатів HTTP-статусам

| Статус | Коли повертається |
|---|---|
| **201 Created** + `Location` | успішне створення ресурсу (`CreatedAtAction`) |
| **200 OK** | успішне отримання або зміна з тілом відповіді |
| **204 No Content** | успіх без тіла (блокування читача, `DELETE`) |
| **400 Bad Request** | помилка валідації (`DataAnnotations` або `ValidationException`) |
| **404 Not Found** | `NotFoundException` — ресурс відсутній (`THROW 50001`) |
| **409 Conflict** | `BusinessConflictException` — недопустимий перехід статусу, брак примірників, порушення унікальності |
| **500** | несподівана інфраструктурна помилка (деталі лише в логах) |

### Тонкі контролери та централізована обробка помилок

У контролерах **немає `try/catch`**: усі винятки перетворює на статуси один
глобальний обробник
[`DomainExceptionHandler`](src/Library.Lending.Api/Infrastructure/DomainExceptionHandler.cs)
(`IExceptionHandler` + `AddProblemDetails()`). Помилки повертаються у
стандартному форматі **ProblemDetails (RFC 9457)**:

```json
{
  "type": "https://httpstatuses.io/409",
  "title": "Business rule violated",
  "status": 409,
  "detail": "Copies can be confirmed only for a new loan",
  "instance": "POST /api/loans/3/confirm",
  "traceId": "0HNQ6IPSCL5RM:00000001",
  "service": "Library.Lending.Api"
}
```

Кожен виняток логується **рівно один раз** — в обробнику.
`CancellationToken` передається з контролера в BLL і далі в DAL; у ланцюжку
немає жодного `.Result` чи `.Wait()`.

---

## Приклади запитів

### curl

```powershell
curl http://localhost:5190/api/loans/3
```

```powershell
curl -X POST http://localhost:5190/api/loans/1/issue
```

```powershell
curl -X POST http://localhost:5190/api/loans -H "Content-Type: application/json" -d "{\"readerId\":1,\"dueDate\":\"2026-12-31\",\"pickupBranch\":\"Central\",\"items\":[{\"bookId\":1,\"units\":1}]}"
```

### PowerShell (коректно надсилає кирилицю)

```powershell
$body = @{ readerId = 1; dueDate = "2026-12-31"; pickupBranch = "Центральна філія"; items = @(@{ bookId = 1; units = 1 }) } | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri http://localhost:5190/api/loans -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8"
```

### Postman

Колекція: [`tests/postman-collection.json`](tests/postman-collection.json) —
імпортуйте й запустіть Runner. Змінна `baseUrl` за замовчуванням
`http://localhost:5190`.

---

## Тестування

### Unit-тести BLL (xUnit + Moq) — 42 тести

```powershell
dotnet test tests/Library.Lending.Tests
```

DAL і Unit of Work замокані, тому тести не торкаються бази й перевіряють саме
**рішення сервісу**. Покрито:

- «happy path» створення, видачі, повернення;
- негативні сценарії: `NotFoundException`, `BusinessConflictException`, `ValidationException`;
- **відкат транзакції при помилці на другому кроці** операції;
- ідемпотентність оплати пені;
- карта допустимих переходів статусів;
- `AssertConfigurationIsValid()` для AutoMapper.

### API-тести (усі критерії приймання) — 61 перевірка

API має бути піднятий, база — у вихідному seed-стані:

```powershell
py -I tests/api-tests.py http://localhost:5190
```

Скрипт перевіряє кожен пункт розділу «Перед здачею перевірте себе»:

| Перевірка з методички | Блок у скрипті | Результат |
|---|---|---|
| значення зі спецсимволом (`O'Brien`) не ламає SQL | 8 | синтаксичної помилки немає |
| спроба SQL-ін'єкції в параметр пошуку | 8 | порожній результат, не всі записи |
| помилка в другому репозиторії транзакції → відкат змін першого | 5 | залишок і статус не змінилися |
| підтвердити вже підтверджену видачу → **409 ProblemDetails** | 3 | 409 + `detail` із процедури |
| неіснуючий Id → **404**, а не 500 | 2 | 404 ProblemDetails |
| кожен ендпоінт у Swagger відповідає документованими статусами | 1–13 | 61/61 |

Збережений вивід: [`../Docs/lab02-api-tests.log`](../Docs/lab02-api-tests.log).

> Скрипт **змінює** стан бази (видає, повертає, оплачує пеню), тому розрахований
> на свіжо засіяну базу й перевіряє це на старті. Якщо передумову не виконано,
> він друкує точну команду перезасівання і завершується кодом 2.

---

## Критерії приймання — відповідність

| Критерій Лаб. 02 | Де виконано |
|---|---|
| **DAL:** інтерфейси, включно з Generic Repository | [`IGenericRepository<T>`](src/Library.Lending.Dal/Abstractions/IGenericRepository.cs), [`Repositories.cs`](src/Library.Lending.Dal/Abstractions/Repositories.cs) |
| **1 реалізація на чистому ADO.NET** | [`ReaderRepository`](src/Library.Lending.Dal/Repositories/ReaderRepository.cs) |
| **2+ реалізації на ADO.NET + Dapper** | `LoanRepository`, `BookRepository`, `FinePaymentRepository` (**3**) |
| спільний CRUD написаний один раз | [`BaseDapperRepository<T>`](src/Library.Lending.Dal/Infrastructure/BaseDapperRepository.cs) |
| усі запити параметризовані | немає жодної конкатенації з вводом; тест 8 |
| **UoW із транзакціями, що охоплює ≥2 репозиторії** | `ConfirmAndIssueAsync`, `CreateAsync`, `PayFineAsync` |
| **BLL:** DTO та профілі AutoMapper, підключені централізовано | [`Dtos.cs`](src/Library.Lending.Bll/Dtos/Dtos.cs), [`LendingProfile`](src/Library.Lending.Bll/Mapping/LendingProfile.cs) |
| сервіси реалізують бізнес-логіку й використовують UoW | [`LoanService`](src/Library.Lending.Bll/Services/LoanService.cs), `ReaderService`, `BookService` |
| доменні винятки NotFound / BusinessConflict / Validation | [`DomainExceptions.cs`](src/Library.Lending.Domain/Exceptions/DomainExceptions.cs) |
| **API:** тонкі контролери без SQL і бізнес-правил | [`Controllers/`](src/Library.Lending.Api/Controllers/) |
| атрибутна маршрутизація | `[Route("api/loans")]`, `[HttpGet("{id:long}")]` |
| асинхронність із `CancellationToken` | усі дії; жодного `.Result` / `.Wait()` |
| коректні HTTP-статуси | таблиця вище; тести 2–4 |
| **ProblemDetails** для помилок | [`DomainExceptionHandler`](src/Library.Lending.Api/Infrastructure/DomainExceptionHandler.cs) |
| **Aspire:** AppHost запускає API разом із базою однією командою | [`AppHost.cs`](src/Library.Lending.AppHost/AppHost.cs) |
| **Документація:** README + OpenAPI/Swagger | цей файл; `/swagger` |
| логування (Serilog, структуроване) | [`appsettings.json`](src/Library.Lending.Api/appsettings.json): Console + File sink |
| тести (додатково) | 42 unit + 61 API-перевірка |

---

## Контроль версій

Репозиторій веде гілки:

- **`main`** — стабільна;
- **`develop`** — інтеграційна;
- **`feature/*`** — окремі завдання (`feature/lab02-dal`, `feature/lab02-bll`, …).

Історія комітів відображає послідовність кроків методички.

---

## Наступна робота

**Лаб. 03** — та сама тришарова архітектура, але на **EF Core** над базою
Проєкту №2 (`LibraryCatalogDb`): Code-First, Fluent API, міграції,
ідемпотентний сідінг, Eager/Explicit loading, пагінація, FluentValidation,
Specification pattern → [`../Lab03-EfCore/`](../Lab03-EfCore/)
