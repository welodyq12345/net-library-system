using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Library.Lending.Api.Infrastructure;

/// <summary>
/// Застосовує SQL-скрипти Лаб. 01 (<c>schema.sql</c> → <c>procedures.sql</c> →
/// <c>seed.sql</c>) до бази при старті застосунку.
/// </summary>
/// <remarks>
/// <para>
/// Потрібен для режиму «усе однією командою»: .NET Aspire піднімає ПОРОЖНІЙ
/// контейнер SQL Server, і схему треба застосувати до нього автоматично.
/// </para>
/// <para>
/// <b>Чому не через Aspire <c>WithCreationScript</c>:</b> Aspire виконує
/// creation script одним пакетом через <c>SqlCommand</c>, а <c>SqlClient</c>
/// <b>не розуміє розділювач <c>GO</c></b> — він є командою клієнта
/// (sqlcmd / SSMS), а не інструкцією T-SQL. Скрипти Лаб. 01 складаються з
/// багатьох пакетів, тому їх потрібно розбити на частини й подати послідовно,
/// що й робить цей клас.
/// </para>
/// <para>
/// <b>Безпека даних:</b> <c>schema.sql</c> видаляє й створює таблиці заново,
/// тому він застосовується ЛИШЕ коли схеми ще немає. Якщо таблиці вже є,
/// виконується тільно <c>seed.sql</c> — він ідемпотентний і дублікатів не
/// створює.
/// </para>
/// </remarks>
public sealed class SqlScriptInitializer
{
    private static readonly Regex GoSeparator = new(
        @"^\s*GO\s*(?:--.*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly ILogger<SqlScriptInitializer> _logger;

    public SqlScriptInitializer(string connectionString, ILogger<SqlScriptInitializer> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>Застосовує скрипти; за потреби чекає, поки база стане доступною.</summary>
    public async Task InitializeAsync(string scriptsDirectory, CancellationToken ct = default)
    {
        if (!Directory.Exists(scriptsDirectory))
        {
            _logger.LogWarning(
                "Каталог SQL-скриптів не знайдено: {Directory}. Ініціалізацію пропущено.",
                scriptsDirectory);
            return;
        }

        await WaitForServerAsync(ct);

        var schemaExists = await SchemaExistsAsync(ct);

        if (schemaExists)
        {
            _logger.LogInformation(
                "Схему вже застосовано — виконую лише ідемпотентний seed.sql.");
            await RunScriptAsync(Path.Combine(scriptsDirectory, "seed.sql"), ct);
        }
        else
        {
            _logger.LogInformation("Схеми немає — застосовую schema.sql, procedures.sql, seed.sql.");
            foreach (var file in new[] { "schema.sql", "procedures.sql", "seed.sql" })
                await RunScriptAsync(Path.Combine(scriptsDirectory, file), ct);
        }

        _logger.LogInformation("Ініціалізацію бази завершено.");
    }

    /// <summary>Чекає доступності сервера: контейнер SQL Server стартує не миттєво.</summary>
    private async Task WaitForServerAsync(CancellationToken ct)
    {
        const int maxAttempts = 30;
        var masterConnectionString = WithDatabase(_connectionString, "master");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(masterConnectionString);
                await connection.OpenAsync(ct);
                await using var command = new SqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(ct);
                return;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                _logger.LogInformation(
                    "Очікую SQL Server... спроба {Attempt}/{Max}", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        throw new InvalidOperationException(
            "SQL Server не став доступним за відведений час");
    }

    private async Task<bool> SchemaExistsAsync(CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.InitialCatalog;

        await using var connection = new SqlConnection(WithDatabase(_connectionString, "master"));
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(
            """
            SELECT CASE
                     WHEN DB_ID(@db) IS NULL THEN 0
                     ELSE (SELECT COUNT(*) FROM sys.databases WHERE name = @db)
                   END
            """, connection);
        command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = databaseName;

        var dbExists = Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
        if (!dbExists)
        {
            _logger.LogInformation("Базу {Database} ще не створено.", databaseName);
            return false;
        }

        // база є — перевіряємо, чи є в ній наші таблиці
        await using var dbConnection = new SqlConnection(_connectionString);
        await dbConnection.OpenAsync(ct);
        await using var tableCheck = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') AND name = 'Loans'",
            dbConnection);

        return Convert.ToInt32(await tableCheck.ExecuteScalarAsync(ct)) > 0;
    }

    /// <summary>
    /// Виконує скрипт, розбивши його на пакети по <c>GO</c>.
    /// </summary>
    private async Task RunScriptAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Скрипт не знайдено: {Path}", path);
            return;
        }

        var sql = await File.ReadAllTextAsync(path, ct);

        // прибираємо CREATE DATABASE / USE: базою керує Aspire або адміністратор,
        // а підключення вже вказує на потрібну базу
        sql = StripDatabaseStatements(sql);

        var batches = GoSeparator.Split(sql)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToList();

        // schema.sql виконуємо в master-контексті лише для CREATE DATABASE;
        // усі інші пакети — у цільовій базі
        var targetConnectionString = await EnsureDatabaseExistsAsync(ct);

        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(ct);

        // налаштування сеансу, обов'язкові для фільтрованих індексів
        await using (var session = new SqlCommand(
            "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;", connection))
        {
            await session.ExecuteNonQueryAsync(ct);
        }

        var executed = 0;
        foreach (var batch in batches)
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            try
            {
                await command.ExecuteNonQueryAsync(ct);
                executed++;
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex,
                    "Помилка у пакеті {Batch} скрипта {Script}: {Message}",
                    executed + 1, Path.GetFileName(path), ex.Message);
                throw;
            }
        }

        _logger.LogInformation(
            "{Script}: виконано {Count} пакет(ів)", Path.GetFileName(path), executed);
    }

    /// <summary>Створює базу, якщо її ще немає, і повертає рядок підключення до неї.</summary>
    private async Task<string> EnsureDatabaseExistsAsync(CancellationToken ct)
    {
        var databaseName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;

        await using var master = new SqlConnection(WithDatabase(_connectionString, "master"));
        await master.OpenAsync(ct);

        // ім'я бази береться з НАШОГО рядка підключення (конфігурація), не з вводу
        // користувача; QUOTENAME додатково екранує його для динамічного SQL
        await using var command = new SqlCommand(
            """
            IF DB_ID(@db) IS NULL
            BEGIN
                DECLARE @sql NVARCHAR(300) = N'CREATE DATABASE ' + QUOTENAME(@db);
                EXEC sp_executesql @sql;
            END
            """, master);
        command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = databaseName;
        await command.ExecuteNonQueryAsync(ct);

        return _connectionString;
    }

    private static string WithDatabase(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }
            .ConnectionString;

    private static string StripDatabaseStatements(string sql)
    {
        var kept = new List<string>();
        var skipping = false;

        foreach (var raw in sql.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("IF DB_ID(", StringComparison.OrdinalIgnoreCase))
            {
                skipping = true;
                continue;
            }
            if (skipping)
            {
                if (trimmed.StartsWith("CREATE DATABASE", StringComparison.OrdinalIgnoreCase))
                    continue;
                skipping = false;
            }

            if (trimmed.StartsWith("USE ", StringComparison.OrdinalIgnoreCase))
                continue;

            kept.Add(line);
        }

        return string.Join("\n", kept);
    }
}
