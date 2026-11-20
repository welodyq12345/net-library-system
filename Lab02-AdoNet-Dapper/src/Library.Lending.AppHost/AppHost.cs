/* =============================================================================
   .NET Aspire AppHost — описує всі ресурси застосунку та їхні залежності.
   Одна команда піднімає і базу даних, і Web/API:

       dotnet run --project src/Library.Lending.AppHost --launch-profile http

   Після старту в консолі з'являється посилання на Aspire Dashboard, де видно
   статус кожного ресурсу (має бути Running) і рядок підключення до бази.
   -----------------------------------------------------------------------------
   ДВА РЕЖИМИ (перемикач — Database:Mode в appsettings.json AppHost):

     "Container"  (за замовчуванням) — Aspire сам піднімає контейнер
                  SQL Server 2022 на порту 14333 і створює порожню базу
                  LibraryLendingDb. Схему, процедури й тестові дані з Лаб. 01
                  застосовує САМ API при старті (див. нижче).

     "Existing"   — використовується вже запущений інстанс SQL Server
                  (наприклад контейнер netlabs-sql із Лаб. 01). Aspire лише
                  передає в API рядок підключення з конфігурації.

   Ім'я ресурсу бази ("LendingDb") збігається з іменем рядка підключення, яке
   читає Library.Lending.Api, тому Aspire передає його в API автоматично —
   код API змінювати не потрібно.
   -----------------------------------------------------------------------------
   ЧОМУ СКРИПТИ НАКАТУЄ API, А НЕ WithCreationScript:
   Aspire виконує creation script ОДНИМ пакетом через SqlCommand, а SqlClient
   не розуміє розділювач GO — це команда клієнта (sqlcmd / SSMS), а не
   інструкція T-SQL. Скрипти Лаб. 01 складаються з багатьох пакетів, тому їх
   потрібно розбити й подати послідовно: це робить SqlScriptInitializer
   у Library.Lending.Api (вмикається Database:ApplyScriptsOnStartup=true,
   що й передається нижче для режиму Container).
   ============================================================================= */

var builder = DistributedApplication.CreateBuilder(args);

var mode = builder.Configuration["Database:Mode"] ?? "Container";
var useContainer = !string.Equals(mode, "Existing", StringComparison.OrdinalIgnoreCase);

IResourceBuilder<IResourceWithConnectionString> lendingDb;

if (useContainer)
{
    // ВАРІАНТ 2 (за замовчуванням): SQL Server у контейнері, яким керує Aspire.
    var sqlPassword = builder.AddParameter("sql-password", secret: true);

    var sqlServer = builder.AddSqlServer("sql", password: sqlPassword, port: 14333)
        .WithDataVolume("library-lending-sqldata")      // дані переживають перезапуск
        .WithLifetime(ContainerLifetime.Persistent);    // контейнер не гаситься між запусками

    lendingDb = sqlServer.AddDatabase("LendingDb", "LibraryLendingDb");
}
else
{
    // ВАРІАНТ 1: існуюча БД з Лаб. 01. Рядок підключення — з appsettings.json
    // AppHost (ConnectionStrings:LendingDb) або зі змінної оточення
    // ConnectionStrings__LendingDb.
    lendingDb = builder.AddConnectionString("LendingDb");
}

// Aspire 17 підключає проєкт за ШЛЯХОМ (AddDotnetProject), а не за згенерованим
// типом Projects.* — узагальнений AddProject<T> оголошено застарілим.
var api = builder.AddDotnetProject("lending-api", "../Library.Lending.Api/Library.Lending.Api.csproj")
    .WithReference(lendingDb)
    .WaitFor(lendingDb)                                 // API стартує після готовності БД
    .WithExternalHttpEndpoints();

if (useContainer)
{
    // база в контейнері створюється ПОРОЖНЬОЮ -> просимо API застосувати
    // schema.sql + procedures.sql + seed.sql з Лаб. 01 при старті
    api.WithEnvironment("Database__ApplyScriptsOnStartup", "true");
}

builder.Build().Run();
