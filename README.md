# Система управління бібліотекою

Навчальний проєкт із дисципліни **«Розробка додатків на платформі .NET /
Технології .NET розробки WEB-додатків»**.

Предметна область розділена на **три незалежні мікросервіси**, кожен зі своєю
базою даних і своєю технологією доступу до даних.

| Робота | Тема | Що зроблено | Статус |
|---|---|---|---|
| [**Лаб. 01**](Lab01-Databases/) | Проєктування БД для мікросервісної архітектури | 3 бази даних: 2 × SQL Server + MongoDB, ERD, 7 збережуваних процедур, таблиця політики дублювання | ✅ |
| [**Лаб. 02**](Lab02-AdoNet-Dapper/) | Тришарова архітектура на **ADO.NET та Dapper** | мікросервіс «Видача книг», Unit of Work із транзакціями, .NET Aspire | ✅ |
| [**Лаб. 03**](Lab03-EfCore/) | Тришарова архітектура на **EF Core** | мікросервіс «Каталог книг», Code-First, міграції, Specification pattern | ✅ |
| [**Лаб. 04**](Lab04-CleanArchitecture-MongoDB/) | **Clean Architecture** і CQRS на MongoDB | мікросервіс «Відгуки на книги», 4 шари, MediatR + Pipeline Behaviors, ETag / If-Match | ✅ |
| [**Лаб. 05**](Lab05-Aspire-Platform/) | Мікросервісна платформа на **.NET Aspire** | AppHost, ServiceDefaults, YARP-шлюз, Aggregator, OpenTelemetry + Serilog | ✅ |

---

## Архітектура

```
┌──────────────────────────────┐   ┌──────────────────────────────┐
│  Library.Lending.Api         │   │  Library.Catalog.Api         │
│  «Видача книг»               │   │  «Каталог книг»              │
│  Лаб. 02                     │   │  Лаб. 03                     │
├──────────────────────────────┤   ├──────────────────────────────┤
│  BLL: сервіси, DTO,          │   │  BLL: сервіси, DTO,          │
│       AutoMapper             │   │       AutoMapper, FluentVal. │
├──────────────────────────────┤   ├──────────────────────────────┤
│  DAL: ADO.NET + Dapper       │   │  DAL: EF Core (Code-First)   │
│       Unit of Work (явні     │   │       Unit of Work (DbContext)│
│       SqlTransaction)        │   │       Specification pattern  │
├──────────────────────────────┤   ├──────────────────────────────┤
│  Domain: моделі, винятки     │   │  Domain: сутності з          │
│                              │   │          бізнес-правилами    │
└──────────────┬───────────────┘   └──────────────┬───────────────┘
               │                                  │
      ┌────────▼─────────┐              ┌─────────▼────────┐
      │ LibraryLendingDb │              │ LibraryCatalogDb │
      │   SQL Server     │              │   SQL Server     │
      └────────┬─────────┘              └─────────┬────────┘
               │                                  │
               └──── ─ ─ ─ логічне посилання ─ ─ ─┘
                     за Id, БЕЗ FOREIGN KEY

                  ┌──────────────────────┐
                  │ LibraryReviewsDb     │   «Відгуки» (Лаб. 04)
                  │      MongoDB         │
                  └──────────────────────┘
```

**Database per Service.** Між базами **немає жодного `FOREIGN KEY`**: посилання
на чужу сутність — звичайний стовпець з ідентифікатором, а цілісність за
потреби перевіряє прикладний код. Узгодженість — **eventual consistency**
через події (Лаб. 08).

---

## Швидкий старт

### 1. Передумови

- **.NET 9 SDK** (прикладні проєкти) + **.NET 10 SDK** (лише для Aspire AppHost)
- **Docker Desktop**
- `dotnet-ef` **9.x**: `dotnet tool install --global dotnet-ef --version 9.0.11`
- Python 3 (для скриптів автоперевірки)

### 2. Підняти СУБД

```powershell
docker run -d --name netlabs-sql -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=LabsDev!Pass2026" -e "MSSQL_PID=Developer" -p 1433:1433 -v netlabs-sqldata:/var/opt/mssql --restart unless-stopped mcr.microsoft.com/mssql/server:2022-latest
```

```powershell
docker run -d --name netlabs-mongo -p 27017:27017 -v netlabs-mongodata:/data/db --restart unless-stopped mongo:7
```

### 3. Розгорнути бази (Лаб. 01)

Детальні команди — у [`Lab01-Databases/README.md`](Lab01-Databases/README.md).

### 4. Запустити сервіси

```powershell
dotnet run --project Lab02-AdoNet-Dapper\src\Library.Lending.AppHost --launch-profile http
```

```powershell
dotnet run --project Lab03-EfCore\src\Library.Catalog.Api --urls http://localhost:5290
```

---

## Три контексти й технології

| Проєкт | Контекст | База даних | Технологія | Чому саме вона |
|---|---|---|---|---|
| №1 | **Видача** | `LibraryLendingDb` | SQL Server + **ADO.NET & Dapper** | транзакційний процес зі статусами й грошима; потрібні збережувані процедури та явні транзакції |
| №2 | **Каталог** | `LibraryCatalogDb` | SQL Server + **EF Core** | багато сутностей і всі типи зв'язків; зручно показати Fluent API, міграції, seed |
| №3 | **Відгуки** | `LibraryReviewsDb` | **MongoDB** | динамічна структура документів: різні читачі лишають різні набори полів |

---

## Результати перевірок

| Що перевірялося | Кількість | Результат |
|---|---|---|
| Лаб. 01 — критерії приймання SQL (`verify.sql`) | 12 блоків | ✅ усі |
| Лаб. 01 — критерії приймання MongoDB (`03-queries.js`) | 11 блоків | ✅ усі |
| Лаб. 02 — unit-тести (xUnit + Moq) | **42** | ✅ 42/42 |
| Лаб. 02 — API-перевірки | **61** | ✅ 61/61 |
| Лаб. 03 — unit-тести (xUnit + InMemory) | **76** | ✅ 76/76 |
| Лаб. 03 — API-перевірки | **73** | ✅ 73/73 |
| Лаб. 04 — unit-тести (домен, CQRS, поведінки) | **72** | ✅ 72/72 |
| Лаб. 04 — API-перевірки | **66** | ✅ 66/66 |
| Лаб. 05 — unit-тести (агрегація, CorrelationId, YARP) | **38** | ✅ 38/38 |
| Лаб. 05 — наскрізні перевірки через Gateway | **44** | ✅ 44/44 |
| Лаб. 05 — сценарій часткової відмови | **6** | ✅ 6/6 |

Збережені звіти — у [`Docs/`](Docs/):

- [`lab02-api-tests.log`](Docs/lab02-api-tests.log) — Лаб. 02, окремий запуск API
- [`lab02-api-tests-aspire.log`](Docs/lab02-api-tests-aspire.log) — Лаб. 02, запуск через .NET Aspire
- [`lab03-api-tests.log`](Docs/lab03-api-tests.log) — Лаб. 03
- [`lab03-eager-vs-explicit.log`](Docs/lab03-eager-vs-explicit.log) — **доказ: Eager = 1 SQL-запит, Explicit = 2**
- [`screenshots/`](Docs/screenshots/) — Swagger обох сервісів, Aspire Dashboard із ресурсами в статусі Running

### Знімки екрана

| Файл | Що показує |
|---|---|
| `lab02-swagger.png` | Swagger «Видача книг» з усіма ендпоінтами |
| `lab02-aspire-dashboard.png` | Aspire Dashboard: `sql`, `LendingDb`, `lending-api` — усі **Running** |
| `lab03-swagger.png` | Swagger «Каталог книг» |
| `lab03-pagination.png` | відповідь із пагінацією (`page=2&pageSize=5`) |

---

## Таблиця політики дублювання даних (коротко)

| Поле | Де зберігається | Джерело (власник) | Політика |
|---|---|---|---|
| `LoanItems.BookTitle`, `BookAuthor`, `DailyFee` | Видача | Каталог | **знімок** на момент оформлення — не оновлюється |
| `Books.Title`, `Author`, `Isbn`, `DailyFee` | Видача | Каталог | копія для читання — **оновлюється подіями** |
| `Books.CopiesAvailable` | Видача | **Видача** | власні дані контексту |
| `reviews.bookTitle` | Відгуки | Каталог | копія для читання — **оновлюється подіями** |
| `reviews.author.displayName` | Відгуки | Видача | **знімок** імені на момент публікації |

Повна таблиця з обґрунтуванням —
[`Lab01-Databases/Docs/domain-and-contexts.md`](Lab01-Databases/Docs/domain-and-contexts.md).

---

## Структура репозиторію

```
LibrarySystem/
├── Lab01-Databases/              Лаб. 01 — проєктування трьох баз даних
│   ├── Docs/                     домен, контексти, ERD, політика дублювання
│   ├── Project1-SqlServer-AdoNet/  schema / procedures (7 шт.) / seed / verify
│   ├── Project2-SqlServer-EfCore/  референсний DDL + seed
│   └── Project3-MongoDB/           колекції, документи, запити й агрегації
├── Lab02-AdoNet-Dapper/          Лаб. 02 — мікросервіс «Видача книг»
│   ├── src/Library.Lending.Domain
│   ├── src/Library.Lending.Dal     ADO.NET + Dapper, Unit of Work
│   ├── src/Library.Lending.Bll
│   ├── src/Library.Lending.Api
│   ├── src/Library.Lending.AppHost .NET Aspire
│   └── tests/                      42 unit + 61 API-перевірка + Postman
├── Lab03-EfCore/                 Лаб. 03 — мікросервіс «Каталог книг»
│   ├── src/Library.Catalog.Domain  сутності з бізнес-правилами в методах
│   ├── src/Library.Catalog.Dal     EF Core, міграції, специфікації
│   ├── src/Library.Catalog.Bll
│   ├── src/Library.Catalog.Api
│   ├── Docs/InitialCreate.sql      згенерований SQL-скрипт міграції
│   └── tests/                      76 unit + 73 API-перевірки + Postman
└── Docs/                         звіти перевірок і знімки екрана
```

---

## Контроль версій

- **`main`** — стабільна гілка
- **`develop`** — інтеграційна гілка
- **`feature/*`** — окремі завдання

Історія комітів відображає послідовність кроків методичок.
`.gitignore` виключає `bin/`, `obj/`, `logs/`, `*.user`.

---

## Звіти

Звіти про виконання всіх п'яти практичних занять — у форматі DOCX:
[`Docs/reports/`](Docs/reports/). Кожен містить мету, завдання, хід роботи з
лістингами коду, рисунки (ERD, Swagger, Aspire Dashboard), результати
тестування, відповіді на контрольні питання та висновки.

Поля титульної сторінки (ПІБ, група, викладач, заклад, місто, рік) залишені
порожніми — їх потрібно заповнити перед здачею.

## Скриншоти

Усі рисунки, на які посилаються звіти, лежать у
[`Docs/screenshots/`](Docs/screenshots/).
