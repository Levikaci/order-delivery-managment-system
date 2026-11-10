using Npgsql;

namespace ApiHost.Db;

/// <summary>Одна версионированная миграция схемы PostgreSQL.</summary>
public sealed record Migration(int Version, string Name, string Sql);

public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Применяет миграции по порядку. Каждая миграция выполняется в отдельной транзакции вместе с записью
/// в schema_migrations: при ошибке откатывается целиком, уже применённые версии и данные не затрагиваются.
/// Таблицы никогда не удаляются автоматически. Параллельные запуски сериализуются advisory-блокировкой.
/// </summary>
public static class Migrator
{
    private const long LockKey = 7_272_001;

    public static int Apply(NpgsqlDataSource ds, IReadOnlyList<Migration> migrations, int? upTo = null, ILogger? log = null)
    {
        Validate(migrations);
        try
        {
            using var c = ds.OpenConnection();
            Exec(c, "SELECT pg_advisory_lock(@k)", ("k", LockKey));
            try
            {
                Exec(c, @"CREATE TABLE IF NOT EXISTS schema_migrations(
                            version int PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())");
                var applied = new HashSet<int>();
                using (var r = new NpgsqlCommand("SELECT version FROM schema_migrations", c).ExecuteReader())
                    while (r.Read()) applied.Add(r.GetInt32(0));

                if (applied.Count == 0 && Scalar(c, "SELECT to_regclass('public.customers') IS NOT NULL") is true)
                    throw new MigrationException("В базе есть таблицы без записей о версии схемы. Автоматическая миграция отменена, чтобы не повредить данные.");
                var known = migrations.Select(m => m.Version).ToHashSet();
                if (applied.FirstOrDefault(v => !known.Contains(v)) is var unknown and > 0)
                    throw new MigrationException($"База создана более новой версией приложения (схема v{unknown}). Обновите приложение.");

                foreach (var m in migrations.OrderBy(m => m.Version))
                {
                    if (applied.Contains(m.Version) || (upTo is not null && m.Version > upTo)) continue;
                    using var tx = c.BeginTransaction();
                    try
                    {
                        Exec(c, m.Sql);
                        Exec(c, "INSERT INTO schema_migrations(version,name) VALUES(@v,@n)", ("v", m.Version), ("n", m.Name));
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        throw new MigrationException($"Миграция {m.Version:0000} «{m.Name}» не применена и откатена: {ex.Message}", ex);
                    }
                    log?.LogInformation("Применена миграция {Version:0000} {Name}", m.Version, m.Name);
                }
                return (int)Scalar(c, "SELECT COALESCE(MAX(version),0) FROM schema_migrations")!;
            }
            finally { Exec(c, "SELECT pg_advisory_unlock(@k)", ("k", LockKey)); }
        }
        catch (NpgsqlException ex) when (ex is not PostgresException)
        {
            throw new MigrationException("Не удалось подключиться к PostgreSQL: " + ex.Message, ex);
        }
    }

    private static void Validate(IReadOnlyList<Migration> ms)
    {
        var versions = ms.Select(m => m.Version).OrderBy(v => v).ToList();
        if (versions.Distinct().Count() != versions.Count || versions.Where((v, i) => v != i + 1).Any())
            throw new MigrationException("Версии миграций должны идти подряд с 1 без повторов.");
    }

    private static void Exec(NpgsqlConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(NpgsqlConnection c, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }
}
