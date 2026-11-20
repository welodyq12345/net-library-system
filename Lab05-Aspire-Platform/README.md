# Практичне заняття № 05 — Мікросервісна платформа на .NET Aspire

**Тема:** Побудова мікросервісної платформи з використанням .NET Aspire,
OpenTelemetry для трасування та Serilog для структурованого логування.

**Предметна область:** Система управління бібліотекою.

Три мікросервіси з Лаб. 02–04 об'єднані в єдину платформу: їх запускає,
налаштовує та зв'язує один оркестратор, перед ними стоїть шлюз, а поряд —
сервіс-агрегатор.

---

## 1. Склад платформи

| Компонент | Проєкт | Роль |
|---|---|---|
| **AppHost** | `src/Library.Platform.AppHost` (net10.0) | Оркестратор: піднімає контейнери БД, усі сервіси, Агрегатор і Gateway однією командою |
| **ServiceDefaults** | `src/Library.Platform.ServiceDefaults` | Спільні наскрізні налаштування: Serilog, OpenTelemetry, health checks, service discovery, CorrelationId, стійкі HttpClient-и |
| **ApiGateway** | `src/Library.Platform.ApiGateway` | Єдина точка входу (порт **5000**) на YARP: маршрутизація, генерація CorrelationId, логування трафіку |
| **Aggregator** | `src/Library.Platform.Aggregator` | Aggregator Pattern: паралельні запити до сервісів, часткові відмови, одна консолідована відповідь |
| **lending-api** | `../Lab02-AdoNet-Dapper` | Видачі: SQL Server + ADO.NET / Dapper |
| **catalog-api** | `../Lab03-EfCore` | Каталог книг: SQL Server + EF Core Code-First |
| **reviews-api** | `../Lab04-CleanArchitecture-MongoDB` | Відгуки: MongoDB + Clean Architecture + CQRS |

Мікросервіси **залишаються на своїх місцях** — у папках своїх лабораторних
робіт. AppHost запускає їх за шляхом до `.csproj`, нічого не копіюючи:
Лаб. 02–04 лишаються самостійними роботами, які можна здати й запустити окремо.

```
                            ┌──────────────┐
          клієнт  ─────────▶│   Gateway    │  :5000   (YARP + CorrelationId)
                            └──────┬───────┘
                       ┌───────────┼────────────┬──────────────┐
                       ▼           ▼            ▼              ▼
                 lending-api  catalog-api  reviews-api   aggregator
                       │           │            │         ╱    │    ╲
                  LendingDb   CatalogDb    ReviewsDb   (Task.WhenAll до трьох
                   (SQL)        (SQL)       (Mongo)      сервісів вище)
```

---

## 2. Запуск

### Передумови

* .NET 9 SDK (сервіси) і .NET 10 SDK (AppHost на Aspire 17).
* **Запущений Docker Desktop** — у ньому працюють контейнери SQL Server і MongoDB.
* Пароль SQL Server — параметр-секрет AppHost. Він уже заданий у секретах
  користувача; якщо репозиторій розгортається з нуля:

```powershell
dotnet user-secrets set "Parameters:sql-password" "LabsDev!Pass2026" --project src/Library.Platform.AppHost
```

### Одна команда

```powershell
dotnet run --project src/Library.Platform.AppHost --launch-profile http
```

Відкриється **Aspire Dashboard** (http://localhost:15026). Усі ресурси
(`sql`, `mongo`, `lending-api`, `catalog-api`, `reviews-api`, `aggregator`,
`gateway`) мають перейти в статус **Running**.

Перший запуск триває довше: Aspire тягне образи, а сервіси самі наповнюють
порожні бази — Видачі накатують скрипти Лаб. 01, Каталог застосовує міграції
EF Core та сідінг, Відгуки створюють індекси й сідінг MongoDB.

Профіль `http` використовується свідомо: профіль `https` вимагає довіреного
сертифіката розробника, і Aspire пропонує його встановити в сховище
довірених коренів системи.

### Точки входу

| Що | Адреса |
|---|---|
| Aspire Dashboard | http://localhost:15026 |
| Gateway (єдина точка входу) | http://localhost:5000 |
| Перелік маршрутів Gateway | http://localhost:5000/ |
| Swagger кожного сервісу | адреси призначає Aspire — дивіться Dashboard |

---

## 3. Маршрути Gateway

| Зовнішній шлях | Кластер | Сервіс |
|---|---|---|
| `/api/aggregator/{**}` | `aggregator` | Агрегатор |
| `/api/loans/{**}` | `lending` | lending-api |
| `/api/readers/{**}` | `lending` | lending-api |
| `/api/lending/books/{**}` | `lending` | lending-api (локальна проєкція довідника) |
| `/api/books/{**}` | `catalog` | catalog-api |
| `/api/authors/{**}` | `catalog` | catalog-api |
| `/api/genres/{**}` | `catalog` | catalog-api |
| `/api/reviews/{**}` | `reviews` | reviews-api |
| `/api/discussions/{**}` | `reviews` | reviews-api |

**Колізія шляхів і як вона вирішена.** Сервіс видач має власну локальну
проєкцію довідника книг на `/api/books` — такий самий шлях, як у Каталогу.
Канонічне джерело даних про книги — Каталог, тому `/api/books` веде в
`catalog-api`, а проєкція Сервісу видач доступна як `/api/lending/books`
(префікс знімає трансформація `PathRemovePrefix`). Цей маршрут має вищий
пріоритет (`Order: 2`), інакше його перехопив би загальніший `/api/books`.

Адреси кластерів — **логічні імена** (`http://lending-api`), а не
`localhost:порт`. Реальну адресу підставляє service discovery Aspire через
`AddServiceDiscoveryDestinationResolver()`.

### Приклади запитів

```powershell
curl.exe -i http://localhost:5000/api/loans/1
curl.exe -i "http://localhost:5000/api/books?page=1&pageSize=5"
curl.exe -i "http://localhost:5000/api/reviews?bookId=1"
curl.exe -i http://localhost:5000/api/aggregator/loans/1/full
curl.exe -i http://localhost:5000/api/aggregator/books/1/card
```

У кожній відповіді є заголовок **`X-Correlation-Id`** — саме його значення
потім шукається у вкладці Structured logs Dashboard.

---

## 4. Агрегатор: паралельність і часткові відмови

`GET /api/aggregator/loans/{id}/full` — повна картка видачі:

| Блок відповіді | Джерело |
|---|---|
| `loan` | lending-api (`/api/loans/{id}`) |
| `reader` | lending-api (`/api/readers/{id}`) |
| `books` | catalog-api (`/api/books/{id}` — по книзі на позицію видачі) |
| `reviews` | reviews-api (`/api/reviews?bookId=…`) |
| `ratings` | reviews-api (`/api/reviews/ratings` — aggregation pipeline MongoDB) |
| `warnings` | сервіси, які не відповіли |
| `meta` | тривалість кожного виклику й виграш від паралельності |

Видача запитується **послідовно** — без неї невідомо, які книги й якого читача
питати. Усе інше виконується **одночасно** через `Task.WhenAll`; блок `meta`
показує це числами:

```json
"meta": {
  "totalMs": 118,
  "sequentialMs": 402,
  "savedByParallelismMs": 284,
  "calls": [
    { "service": "catalog-api",        "status": "ok", "elapsedMs": 96 },
    { "service": "lending-api/loans",  "status": "ok", "elapsedMs": 41 },
    { "service": "reviews-api/reviews","status": "ok", "elapsedMs": 77 }
  ]
}
```

**Часткова відмова.** Якщо сервіс недоступний (`HttpRequestException`) або не
встиг відповісти (`TaskCanceledException`), виклик не «валить» запит: блок
лишається порожнім, причина потрапляє в `warnings`, статус відповіді — 200.
Інші винятки **не** поглинаються: помилка самого Агрегатора має дійти до
глобального обробника й стати 500.

Перевірка (контрольна точка методички):

1. у Dashboard зупинити ресурс `reviews-api`;
2. виконати `curl.exe -i http://localhost:5000/api/aggregator/loans/1/full`;
3. у відповіді є `loan`, `reader`, `books`, а в `warnings` —
   `reviews-api/reviews is unavailable: …`;
4. запустити `reviews-api` знову — відповідь знову повна.

---

## 5. ServiceDefaults — що саме дає один рядок

`builder.AddServiceDefaults()`:

* **Serilog** — структуроване логування з `CompactJsonFormatter`,
  enrichers `ServiceName`, `Environment`, `MachineName`, `ThreadId`,
  `LogContext`; рівень `Debug` у Development, `Information` у Production;
  шумні логи інфраструктури (EF Core, HttpClient) приглушені до `Warning`.
  Прапорець **`writeToProviders: true`** — ключовий: вкладка Structured logs
  отримує логи по OTLP, а не з консолі, тому Serilog має передавати записи
  далі стандартним провайдерам, серед яких провайдер OpenTelemetry.
* **OpenTelemetry** — трасування вхідних запитів (ASP.NET Core), вихідних
  викликів (HttpClient), SQL (`SqlClient` — покриває і Dapper з Лаб. 02, і
  EF Core з Лаб. 03) та MongoDB (`MongoDB.Driver.Core.Extensions.DiagnosticSources`);
  метрики ASP.NET Core / HttpClient / Runtime; OTLP-експорт, коли Aspire
  передав `OTEL_EXPORTER_OTLP_ENDPOINT`. Health-перевірки з трасування
  відфільтровані, щоб не засмічувати Traces.
* **Health checks** — `/alive` (лише живучість, тег `live`) і повна перевірка.
* **Service discovery** — логічні імена замість адрес.
* **Стійкі HttpClient-и** — `AddStandardResilienceHandler()` (повтори,
  circuit breaker, таймаути) + `CorrelationIdHandler` для всіх клієнтів.

`app.UseServiceDefaults()` — `CorrelationIdMiddleware` + логування HTTP-запитів.

### CorrelationId ≠ TraceId

| | CorrelationId | TraceId |
|---|---|---|
| Хто створює | сам застосунок (Gateway або сервіс) | OpenTelemetry |
| Призначення | бізнес-ідентифікатор звернення, його можна назвати в підтримці | технічне трасування |
| Де видно | заголовок `X-Correlation-Id`, властивість у кожному лозі | Traces, поля TraceId/SpanId у логах |

Middleware реєструється **в кожному сервісі**, а не лише в Gateway: до сервісу
можуть звернутися напряму, в обхід шлюзу, і тоді ідентифікатор треба створити
на місці.

---

## 6. Що змінилося в сервісах Лаб. 02–04

Контрольна точка методички: *«у коді сервісів змінено лише `Program.cs`»*.

У кожному з трьох сервісів змінено **тільки `Program.cs`**:

```diff
- builder.Host.UseSerilog((ctx, cfg) => cfg …);      // власна конфігурація Serilog
+ builder.AddServiceDefaults();                      // Serilog + OTel + health + discovery

- app.UseSerilogRequestLogging();
+ app.UseServiceDefaults();                          // CorrelationId + логування запитів

  app.MapControllers();
+ app.MapDefaultEndpoints("/health/ready");          // /alive і /health/ready
```

плюс `ProjectReference` на `Library.Platform.ServiceDefaults` у `.csproj`.
Бізнес-логіка, контролери, репозиторії, доменні моделі й міграції **не
змінені** — переконатися можна в історії git.

**Два свідомих відступи від методички, обидва через конфлікт із Лаб. 02–04:**

1. **`/health` vs `MapDefaultEndpoints()`.** У сервісів Лаб. 02–04 уже є
   власний `/health`, який перевіряє саме свою базу (і на нього спираються
   їхні API-тести). Другий маршрут з тим самим шаблоном дав би
   `AmbiguousMatchException` під час запиту, тому `MapDefaultEndpoints`
   приймає шлях параметром, а сервіси передають `/health/ready`. Gateway і
   Агрегатор власного `/health` не мають і використовують шлях за
   замовчуванням.

2. **Трасування MongoDB** (методичка, ч. 4.3) вимагає підписати драйвер на
   діагностичні події там, де створюється `MongoClient` — а це
   `MongoDbContext` у шарі Infrastructure Лаб. 04, не `Program.cs`:

   ```csharp
   settings.ClusterConfigurator = cb => cb.Subscribe(new DiagnosticsActivityEventSubscriber());
   ```

   Це інфраструктурне наскрізне налаштування, а не бізнес-логіка; без
   слухача (поза платформою) підписка не має жодного ефекту.

### Рядки підключення

Aspire передає їх у змінних оточення, а імена ресурсів в AppHost збігаються з
іменами, які сервіси вже читали з конфігурації, тому код чіпати не довелося:

| Сервіс | Що читає сервіс | Що передає AppHost |
|---|---|---|
| lending-api | `ConnectionStrings:LendingDb` | `WithReference(lendingDb)` → `ConnectionStrings__LendingDb` |
| catalog-api | `ConnectionStrings:CatalogDb` | `WithReference(catalogDb)` → `ConnectionStrings__CatalogDb` |
| reviews-api | `MongoDb:ConnectionString` | `WithEnvironment("MongoDb__ConnectionString", reviewsDb)` |

Сервіс відгуків читає не `ConnectionStrings`, а власну секцію `MongoDb`
(Лаб. 04), тому рядок підключення передається прямо в потрібну змінну
оточення — замість того, щоб переписувати сервіс під платформу.

Резервні рядки підключення в `appsettings.json` сервісів лишилися, тому кожен
сервіс і далі запускається самостійно, без Aspire.

---

## 7. Моніторинг у Dashboard

**Resources** — усі сім ресурсів у статусі Running, порти, змінні оточення.

**Traces** — повний шлях одного запиту:

```
gateway  GET /api/aggregator/loans/1/full
└── aggregator  GET /api/aggregator/loans/1/full
    ├── AggregateFullLoan                        (власний span Агрегатора)
    ├── lending-api  GET /api/loans/1  →  SQL: usp_… / SELECT
    ├── lending-api  GET /api/readers/7 →  SQL: SELECT          ┐
    ├── catalog-api  GET /api/books/10  →  SQL: SELECT          │ паралельно
    ├── reviews-api  GET /api/reviews   →  mongodb: find        │
    └── reviews-api  GET /api/reviews/ratings → mongodb: aggregate ┘
```

Паралельність видно за тим, що spans трьох сервісів **перекриваються** на
шкалі часу, а не йдуть один за одним.

**Structured logs** — фільтр за `CorrelationId` (значення беремо з заголовка
відповіді) показує логи **всіх** сервісів ланцюжка. Кожен запис містить
`ServiceName`, `Environment`, `MachineName`, `ThreadId`, `TraceId`, `SpanId`.

**Metrics** — `http.server.request.duration`, `http.client.request.duration`,
метрики Runtime для кожного сервісу.

---

## 8. Тести

### Модульні (35 тестів)

```powershell
dotnet test tests/Library.Platform.Tests
```

| Група | Що перевіряється |
|---|---|
| `AggregationTests` | склад агрегованої відповіді; `null` → 404, коли видачі немає; часткові відмови (Каталог / Відгуки / таймаут); несподівана помилка **не** поглинається; **паралельність** (сумарний час менший за послідовний); передача `CancellationToken`; відкидання даних чужих книг; дедуплікація позицій; картка книги |
| `CorrelationIdMiddlewareTests` | генерація GUID за відсутності заголовка; збереження ідентифікатора клієнта; порожній заголовок = відсутній; потрапляння `CorrelationId` у **всі** логи запиту через `LogContext` |
| `CorrelationIdHandlerTests` | перенесення ідентифікатора у вихідні запити; поведінка поза HTTP-запитом; явно заданий заголовок не перезаписується |
| `ServiceDefaultsTests` | health check `self` з тегом `live`; реєстрація `CorrelationIdHandler` і `IHttpContextAccessor`; Serilog за стандартним `ILogger<T>`; збирання конвеєра типізованого клієнта |
| `GatewayRouteConfigurationTests` | кластер на кожен сервіс; адреси — логічні імена без портів; усі маршрути вказують на наявні кластери; пріоритети (`/api/lending/books` раніше за `/api/books`); catch-all шаблони; метадані `Team`/`Version`; обмежений `ActivityTimeout` |

### Наскрізні через Gateway

Запустіть платформу, потім:

```powershell
python tests/api-tests.py
```

Скрипт звертається **лише через Gateway (порт 5000)** і перевіряє
маршрутизацію до всіх трьох сервісів, наявність `X-Correlation-Id`,
проходження власного ідентифікатора клієнта наскрізь, роботу агрегувальних
ендпоінтів, паралельність за блоком `meta` і операції запису.

---

## 9. Відповіді на контрольні питання

**Яку роль виконують AppHost і ServiceDefaults? Чому AppHost не повинен
містити бізнес-логіки?**
AppHost описує *ресурси* системи та їхні залежності й керує їхнім життєвим
циклом під час розробки; ServiceDefaults — бібліотека *наскрізних* налаштувань,
яку підключає кожен компонент. AppHost не потрапляє в розгортання: у
production його роль виконують Kubernetes, Compose або інфраструктура
хмарного провайдера. Бізнес-логіка в AppHost просто зникла б при розгортанні.

**Як працює service discovery в Aspire? Чому ім'я ресурсу можна
використовувати як адресу?**
AppHost призначає кожному ресурсу порт і передає залежним компонентам змінні
оточення виду `services__catalog-api__http__0=http://localhost:1234`. Бібліотека
`Microsoft.Extensions.ServiceDiscovery` перехоплює запити до
`http://catalog-api` та підставляє реальну адресу зі цих змінних. Схема
`https+http://` означає «HTTPS, якщо сервіс його має, інакше HTTP».

**Яка різниця між `WithReference()` і `WaitFor()`? Чому часто потрібні обидва?**
`WithReference()` передає *конфігурацію* — рядок підключення або адресу
сервісу. `WaitFor()` керує *порядком запуску*. Передана адреса не гарантує,
що адресат готовий: без `WaitFor` сервіс стартував би раніше за свою базу й
упав на першому запиті.

**Чим CorrelationId відрізняється від TraceId? Навіщо системі обидва?**
TraceId генерує OpenTelemetry для технічного трасування, він живе в межах
телеметрії. CorrelationId створює й передає сам застосунок — це бізнес-
ідентифікатор звернення: він повертається клієнту в заголовку, його можна
назвати в підтримці, і він лишається стабільним незалежно від налаштувань
трасування (наприклад, при sampling, коли частина трасувань не зберігається).

---

## 10. Структура проєкту

```
Lab05-Aspire-Platform/
├── Library.Platform.slnx                    # рішення: платформа + три сервіси
├── src/
│   ├── Library.Platform.AppHost/            # оркестратор (net10.0, Aspire 17)
│   │   ├── Program.cs                       # ресурси, WithReference / WaitFor
│   │   └── Properties/launchSettings.json   # порти Dashboard і OTLP
│   ├── Library.Platform.ServiceDefaults/
│   │   ├── Extensions.cs                    # Serilog, OpenTelemetry, health, discovery
│   │   └── CorrelationId.cs                 # middleware + DelegatingHandler
│   ├── Library.Platform.ApiGateway/
│   │   ├── Program.cs                       # YARP + логування маршрутизації
│   │   └── appsettings.json                 # Routes і Clusters
│   └── Library.Platform.Aggregator/
│       ├── Clients/Clients.cs               # типізовані HttpClient-и
│       ├── Dtos/Dtos.cs                     # власні контракти Агрегатора
│       ├── Services/AggregationService.cs   # Task.WhenAll + часткові відмови
│       └── Controllers/AggregatorController.cs
└── tests/
    ├── Library.Platform.Tests/              # 35 модульних тестів
    └── api-tests.py                         # наскрізні перевірки через Gateway
```
