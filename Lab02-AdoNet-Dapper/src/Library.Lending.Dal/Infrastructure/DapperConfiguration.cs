using System.Data;
using Dapper;

namespace Library.Lending.Dal.Infrastructure;

/// <summary>
/// Одноразове налаштування Dapper. Викликається при старті застосунку.
/// </summary>
public static class DapperConfiguration
{
    private static bool _configured;
    private static readonly object Gate = new();

    public static void Configure()
    {
        if (_configured) return;

        lock (Gate)
        {
            if (_configured) return;

            // Стовпці БД у PascalCase збігаються з іменами властивостей,
            // тому власний матчер імен не потрібен. Явно вимикаємо
            // підтримку підкреслень, щоб поведінка не залежала від версії.
            DefaultTypeMap.MatchNamesWithUnderscores = false;

            // DateOnly / TimeOnly: мапимо на DATE / TIME незалежно від того,
            // чи підтримує їх конкретна версія Microsoft.Data.SqlClient нативно.
            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
            SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());

            _configured = true;
        }
    }

    private sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => value switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            DateOnly d  => d,
            string s    => DateOnly.Parse(s),
            _           => throw new DataException($"Cannot convert {value?.GetType()} to DateOnly")
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }
    }

    private sealed class NullableDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override DateOnly? Parse(object? value) => value switch
        {
            null        => null,
            DBNull      => null,
            DateTime dt => DateOnly.FromDateTime(dt),
            DateOnly d  => d,
            string s    => DateOnly.Parse(s),
            _           => throw new DataException($"Cannot convert {value.GetType()} to DateOnly?")
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.HasValue
                ? value.Value.ToDateTime(TimeOnly.MinValue)
                : DBNull.Value;
        }
    }
}
