# Лабораторна робота № 01 — Проєктування та розробка баз даних для мікросервісної архітектури

**Предметна область:** Система управління бібліотекою
**Тема:** ASP.NET. Проєктування та розробка баз даних для мікросервісної архітектури

---

## Зміст

| Документ | Що містить |
|---|---|
| [`Docs/domain-and-contexts.md`](Docs/domain-and-contexts.md) | Опис домену, 3 обмежені контексти, агрегати, **таблиця політики дублювання даних**, обґрунтування PK, аудит/конкурентність, статуси й переходи |
| [`Docs/erd-diagrams.md`](Docs/erd-diagrams.md) | **ERD-діаграми** Проєктів №1 і №2 (Mermaid + DSL для dbdiagram.io), карта колекцій MongoDB, таблиці індексів із призначенням |
| [`Project1-SqlServer-AdoNet/`](Project1-SqlServer-AdoNet/) | `schema.sql`, `procedures.sql` (**7 процедур**), `seed.sql`, `verify.sql` |
| [`Project2-SqlServer-EfCore/`](Project2-SqlServer-EfCore/) | `schema.sql` (референсний DDL), `seed.sql` |
| [`Project3-MongoDB/`](Project3-MongoDB/) | `01-create-collections.js`, `02-seed.js`, `03-queries.js` |

---

## Три бази даних

| Проєкт | База даних | Контекст | Технологія | Реалізація |
|---|---|---|---|---|
| **№1** | `LibraryLendingDb` | Видача книг | SQL Server + ADO.NET & Dapper | Лаб. 02 |
| **№2** | `LibraryCatalogDb` | Каталог книг | SQL Server + EF Core (Code First) | Лаб. 03 |
| **№3** | `LibraryReviewsDb` | Відгуки й обговорення | MongoDB | Лаб. 04 |

Кожна база — **самостійна одиниця** зі власною схемою. Між базами **немає жодного
`FOREIGN KEY`**: посилання на чужу сутність зберігається як звичайний стовпець з
ідентифікатором, а цілісність за потреби перевіряє прикладний код.

---

## Програмне забезпечення

- **SQL Server 2022** — у контейнері Docker (або локальний інстанс / LocalDB)
- **MongoDB 7** — у контейнері Docker (або локальна служба)
- **mongosh** — оболонка MongoDB (входить до образу `mongo:7`)
- **sqlcmd** — входить до образу `mssql/server:2022-latest`

---

## Розгортання з нуля

### Крок 0. Підняти СУБД

```powershell
docker run -d --name netlabs-sql -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=LabsDev!Pass2026" -e "MSSQL_PID=Developer" -p 1433:1433 -v netlabs-sqldata:/var/opt/mssql --restart unless-stopped mcr.microsoft.com/mssql/server:2022-latest
```

```powershell
docker run -d --name netlabs-mongo -p 27017:27017 -v netlabs-mongodata:/data/db --restart unless-stopped mongo:7
```

Рядки підключення:

| База | Рядок підключення |
|---|---|
| `LibraryLendingDb` | `Server=localhost,1433;Database=LibraryLendingDb;User Id=sa;Password=LabsDev!Pass2026;TrustServerCertificate=True;Encrypt=False` |
| `LibraryCatalogDb` | `Server=localhost,1433;Database=LibraryCatalogDb;User Id=sa;Password=LabsDev!Pass2026;TrustServerCertificate=True;Encrypt=False` |
| `LibraryReviewsDb` | `mongodb://localhost:27017/LibraryReviewsDb` |

> **Найменші привілеї.** Для навчального стенду використано `sa`. У бойовому
> середовищі кожен сервіс отримує **власний логін** із доступом лише до своєї БД:
> ```sql
> CREATE LOGIN lending_app WITH PASSWORD = '...';
> USE LibraryLendingDb;
> CREATE USER lending_app FOR LOGIN lending_app;
> ALTER ROLE db_datareader ADD MEMBER lending_app;
> ALTER ROLE db_datawriter ADD MEMBER lending_app;
> GRANT EXECUTE ON SCHEMA::dbo TO lending_app;   -- для збережуваних процедур
> ```

### Крок 1. Проєкт №1 — `LibraryLendingDb`

Порядок обов'язковий: **schema → procedures → seed → verify**.

```powershell
docker cp "Project1-SqlServer-AdoNet" netlabs-sql:/tmp/p1
```

```powershell
docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "LabsDev!Pass2026" -C -i /tmp/p1/schema.sql
```

```powershell
docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "LabsDev!Pass2026" -C -i /tmp/p1/procedures.sql
```

```powershell
docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "LabsDev!Pass2026" -C -i /tmp/p1/seed.sql
```

```powershell
docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "LabsDev!Pass2026" -C -i /tmp/p1/verify.sql
```

### Крок 2. Проєкт №2 — `LibraryCatalogDb`

Базу **створює EF Core міграціями в Лаб. 03**:

```powershell
dotnet ef database update --project ..\Lab03-EfCore\src\Library.Catalog.Dal --startup-project ..\Lab03-EfCore\src\Library.Catalog.Api
```

Сідінг виконується автоматично при старті API (`DbInitializer.SeedAsync`).

Щоб подивитися цільову схему **без** EF Core, можна виконати референсний DDL:

```powershell
docker cp "Project2-SqlServer-EfCore" netlabs-sql:/tmp/p2
```

```powershell
docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "LabsDev!Pass2026" -C -i /tmp/p2/schema.sql -i /tmp/p2/seed.sql
```

> Увага: якщо ви виконали референсний DDL вручну, перед запуском міграцій
> видаліть базу (`DROP DATABASE LibraryCatalogDb`), щоб EF Core створив її зі
> своїх моделей.

### Крок 3. Проєкт №3 — `LibraryReviewsDb`

```powershell
docker cp "Project3-MongoDB" netlabs-mongo:/tmp/p3
```

```powershell
docker exec netlabs-mongo mongosh "mongodb://localhost:27017" --quiet --file /tmp/p3/01-create-collections.js
```

```powershell
docker exec netlabs-mongo mongosh "mongodb://localhost:27017" --quiet --file /tmp/p3/02-seed.js
```

```powershell
docker exec netlabs-mongo mongosh "mongodb://localhost:27017" --quiet --file /tmp/p3/03-queries.js
```

### Крок 4. Переглянути таблицю політики дублювання

[`Docs/domain-and-contexts.md`](Docs/domain-and-contexts.md) → розділ **5**.

---

## Таблиця політики дублювання даних (коротко)

| Поле | Де зберігається | Джерело (власник) | Політика узгодженості |
|---|---|---|---|
| `LoanItems.BookTitle`, `BookAuthor`, `DailyFee` | `LibraryLendingDb` | `Books` у `LibraryCatalogDb` | **знімок** на момент оформлення — не оновлюється |
| `Books.Title`, `Author`, `Isbn`, `DailyFee` | `LibraryLendingDb` | `Books` у `LibraryCatalogDb` | копія для читання — **оновлюється подіями** з Каталогу |
| `Books.CopiesAvailable` | `LibraryLendingDb` | **власні дані** контексту Видачі | змінюється операціями видачі/повернення |
| `reviews.bookTitle`, `discussions.bookTitle`, `readingLists.items[].bookTitle` | `LibraryReviewsDb` | `Books` у `LibraryCatalogDb` | копія для читання — **оновлюється подіями** з Каталогу |
| `reviews.author.displayName`, `readingLists.owner.displayName` | `LibraryReviewsDb` | `Readers` у `LibraryLendingDb` | **знімок** імені на момент публікації |

Повна таблиця з обґрунтуванням — у [`Docs/domain-and-contexts.md`](Docs/domain-and-contexts.md).

---

## Збережувані процедури Проєкту №1 (7 штук)

| Процедура | Призначення | Транзакція | Коди помилок |
|---|---|---|---|
| `usp_CreateLoan` | створення видачі + позицій зі **знімками** полів каталогу | явна | 50001, 50004 |
| `usp_ConfirmLoanCopies` | `Submitted/AwaitingValidation` → `CopiesConfirmed` | явна + `UPDLOCK` | **50002**, 50003, 50004 |
| `usp_IssueLoan` | `CopiesConfirmed` → `Issued`, зменшує залишок примірників | явна + `UPDLOCK` | 50002, 50003 |
| `usp_ReturnLoan` | `Issued` → `Returned`, повертає примірники, **рахує пеню** | явна + `UPDLOCK` | 50002 |
| `usp_CancelLoan` | → `Cancelled`, **лише до видачі на руки** | явна + `UPDLOCK` | 50002 |
| `usp_DecreaseBookCopies` | точкове зменшення залишку з перевіркою | `UPDLOCK` | 50001, 50003, 50004 |
| `usp_GetReaderLoanSummary` | читання/звіт по видачах читача | — | 50001 |

Коди помилок перетворюються на HTTP-статуси в Лаб. 02:

| Код | Доменний виняток | HTTP |
|---|---|---|
| 50001 | `NotFoundException` | **404** |
| 50002 | `BusinessConflictException` | **409** |
| 50003 | `BusinessConflictException` | **409** |
| 50004 | `BusinessConflictException` | **409** |

---

## Перевірка критеріїв приймання

`verify.sql` (Проєкт №1) і `03-queries.js` (Проєкт №3) автоматично перевіряють
кожен пункт розділу «Перед здачею перевірте себе»:

| Критерій | Де перевіряється | Очікуваний результат |
|---|---|---|
| `SELECT * FROM sys.foreign_keys` не показує посилань за межі бази | `verify.sql` блок 1 | лише внутрішні FK |
| 1:1, 1:N, M:N присутні | `verify.sql` блок 2 | три рядки з реалізацією |
| PK/FK/UNIQUE/CHECK/DEFAULT | `verify.sql` блок 3 | перелік із кількостями |
| індекси та їх призначення | `verify.sql` блок 4 | перелік + коментарі в `schema.sql` |
| 7 збережуваних процедур | `verify.sql` блок 5 | перелік |
| **недопустимий перехід статусу → керована помилка** | `verify.sql` блок 6 | `OK  Процедура відмовила. Код = 50002` + стан не змінився |
| **неіснуючий Id → помилка 50001** | `verify.sql` блок 7 | `OK  Код = 50001` |
| **брак примірників → 50003 + відкат** | `verify.sql` блок 8 | `OK  Залишок не змінився` |
| спецсимвол `O'Brien` у даних | `verify.sql` блок 9 | рядок збережено коректно |
| знімки полів каталогу | `verify.sql` блок 10 | знімок ≠ поточна копія довідника |
| аудитні колонки + `rowversion` | `verify.sql` блок 11 | перелік колонок |
| `db.<колекція>.find()` → **різні набори полів** | `03-queries.js` блок 1 | кожен документ має свій перелік полів |
| embedding / referencing / гібрид | `03-queries.js` блоки 2–3 | `$lookup` + вбудовані масиви |
| JSON Schema Validation відхиляє некоректне | `03-queries.js` блок 9 | `OK  Валідатор відхилив документ` |
| індекси MongoDB використовуються | `03-queries.js` блок 11 | `IXSCAN`, а не `COLLSCAN` |
| **повторний запуск seed не створює дублікатів** | `seed.sql` / `02-seed.js` | однакові `COUNT(*)` після двох запусків |

### Перевірка ідемпотентності сідінгу

```powershell
docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "LabsDev!Pass2026" -C -i /tmp/p1/seed.sql -i /tmp/p1/seed.sql
```

Обидва прогони мають вивести однакові значення `COUNT(*)`.

---

## Наступні роботи

- **Лаб. 02** — тришарова архітектура на **ADO.NET та Dapper** над `LibraryLendingDb`
  ([`../Lab02-AdoNet-Dapper/`](../Lab02-AdoNet-Dapper/))
- **Лаб. 03** — тришарова архітектура на **EF Core** над `LibraryCatalogDb`
  ([`../Lab03-EfCore/`](../Lab03-EfCore/))
- **Лаб. 04** — реалізація над `LibraryReviewsDb` (MongoDB)
