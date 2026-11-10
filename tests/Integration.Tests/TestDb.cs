using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ApiHost;
using Npgsql;

namespace Integration.Tests;

/// <summary>
/// Отдельная база PostgreSQL на каждый тест (создаётся и удаляется автоматически).
/// Сервер задаётся переменной TEST_PG_ADMIN (строка подключения с правом CREATE DATABASE),
/// по умолчанию — localhost:5432, postgres/postgres (как в docker-compose и CI).
/// </summary>
public sealed class TestDb : IDisposable
{
    public static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("TEST_PG_ADMIN") ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres";

    public string Name { get; } = "t_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; }

    public TestDb()
    {
        Admin($"CREATE DATABASE {Name}");
        ConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = Name }.ToString();
    }

    public IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString }).Build();

    public Store OpenStore() => new(Config(), NullLogger<Store>.Instance);

    /// <summary>Выполнить SQL напрямую (подготовка данных, ловушки для проверки отказов).</summary>
    public void Sql(string sql)
    {
        using var c = new NpgsqlConnection(ConnectionString);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql)
    {
        using var c = new NpgsqlConnection(ConnectionString);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        return (T)cmd.ExecuteScalar()!;
    }

    private static void Admin(string sql)
    {
        using var c = new NpgsqlConnection(AdminConnectionString);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        Admin($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");
    }
}
