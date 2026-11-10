using System.Globalization;
using ApiHost.Db;
using Contracts;
using Microsoft.Data.Sqlite;
using Npgsql;
using OrderModule;

namespace SqliteImport;

public sealed class ImportException(string message) : Exception(message);

/// <summary>
/// Разовый перенос данных из SQLite (схема v2 прежней версии) в PostgreSQL.
/// Исходный файл только читается и не изменяется. Перенос выполняется в одной транзакции: либо всё, либо ничего.
/// Цены REAL → копейки (bigint); заказ с неудачной доставкой (раньше Cancelled) получает статус DeliveryFailed.
/// </summary>
public static class SqliteImporter
{
    public const int SupportedSqliteVersion = 2;

    public static IReadOnlyDictionary<string, long> Run(string sqlitePath, NpgsqlDataSource target)
    {
        if (!File.Exists(sqlitePath)) throw new ImportException($"Файл SQLite не найден: {sqlitePath}");
        var counts = new Dictionary<string, long>();
        using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        src.Open();
        var ver = Convert.ToInt32(Scalar(src, "PRAGMA user_version"));
        if (ver != SupportedSqliteVersion)
            throw new ImportException($"Поддерживается только схема SQLite v{SupportedSqliteVersion}, в файле v{ver}. Сначала запустите прежнюю версию приложения, чтобы обновить файл.");

        Migrator.Apply(target, Migrations.All);
        using var dst = target.OpenConnection();
        using var tx = dst.BeginTransaction();
        if (Count(dst, "SELECT COUNT(*) FROM customers") + Count(dst, "SELECT COUNT(*) FROM users") + Count(dst, "SELECT COUNT(*) FROM orders") > 0)
            throw new ImportException("Целевая база PostgreSQL не пуста: импорт возможен только в чистую базу (после первого запуска API демо-данные нужно удалить).");

        var failedOrders = new HashSet<string>();
        using (var r = Query(src, "SELECT order_id FROM deliveries WHERE status='Failed'"))
            while (r.Read()) failedOrders.Add(r.GetString(0));

        Copy(src, dst, counts, "customers", "SELECT id,name,phone FROM customers",
            "INSERT INTO customers(id,name,phone) VALUES(@0,@1,@2)", r => new object?[] { G(r, 0), r.GetString(1), r.GetString(2) });
        Copy(src, dst, counts, "customer_addresses", "SELECT id,customer_id,label,city,street,house,apartment FROM customer_addresses",
            "INSERT INTO customer_addresses(id,customer_id,label,city,street,house,apartment) VALUES(@0,@1,@2,@3,@4,@5,@6)",
            r => new object?[] { G(r, 0), G(r, 1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), S(r, 6) });
        Copy(src, dst, counts, "couriers", "SELECT id,name,phone,state FROM couriers",
            "INSERT INTO couriers(id,name,phone,state) VALUES(@0,@1,@2,@3)", r => new object?[] { G(r, 0), r.GetString(1), r.GetString(2), r.GetString(3) });
        Copy(src, dst, counts, "products", "SELECT id,name,price FROM products",
            "INSERT INTO products(id,name,price_kopecks) VALUES(@0,@1,@2)", r => new object?[] { G(r, 0), r.GetString(1), Kopecks(r, 2) });
        Copy(src, dst, counts, "users", "SELECT id,login,salt,hash,role,ref_id FROM users",
            "INSERT INTO users(id,login,salt,hash,role,ref_id) VALUES(@0,@1,@2,@3,@4,@5)",
            r => new object?[] { G(r, 0), r.GetString(1), (byte[])r["salt"], (byte[])r["hash"], r.GetString(4), r.IsDBNull(5) ? null : G(r, 5) });
        Copy(src, dst, counts, "orders", "SELECT id,customer_id,address,comment,status,created_at FROM orders",
            "INSERT INTO orders(id,customer_id,address,comment,status,created_at) VALUES(@0,@1,@2,@3,@4,@5)",
            r => new object?[] { G(r, 0), G(r, 1), r.GetString(2), S(r, 3), OrderStatusOf(r.GetString(0), r.GetString(4), failedOrders), T(r, 5) });
        Copy(src, dst, counts, "order_items", "SELECT order_id,product_id,name,price,quantity FROM order_items ORDER BY id",
            "INSERT INTO order_items(order_id,product_id,name,price_kopecks,quantity) VALUES(@0,@1,@2,@3,@4)",
            r => new object?[] { G(r, 0), G(r, 1), r.GetString(2), Kopecks(r, 3), r.GetInt32(4) });
        Copy(src, dst, counts, "deliveries", "SELECT id,order_id,courier_id,status,created_at,started_at,finished_at,failure_reason,confirmed_at FROM deliveries",
            "INSERT INTO deliveries(id,order_id,courier_id,status,created_at,started_at,finished_at,failure_reason,confirmed_at) VALUES(@0,@1,@2,@3,@4,@5,@6,@7,@8)",
            r => new object?[] { G(r, 0), G(r, 1), r.IsDBNull(2) ? null : G(r, 2), r.GetString(3), T(r, 4), TN(r, 5), TN(r, 6), S(r, 7), TN(r, 8) });
        Copy(src, dst, counts, "status_history", "SELECT delivery_id,status,at,comment FROM status_history ORDER BY id",
            "INSERT INTO status_history(delivery_id,status,at,comment) VALUES(@0,@1,@2,@3)",
            r => new object?[] { G(r, 0), r.GetString(1), T(r, 2), S(r, 3) });
        Copy(src, dst, counts, "order_history", "SELECT order_id,status,at,comment FROM order_history ORDER BY id",
            "INSERT INTO order_history(order_id,status,at,comment) VALUES(@0,@1,@2,@3)",
            r => new object?[] { G(r, 0), OrderHistoryStatus(r.GetString(0), r.GetString(1), S(r, 3), failedOrders), T(r, 2), S(r, 3) });

        tx.Commit();
        return counts;
    }

    // Раньше Failed превращался в Cancelled; настоящие отмены остаются Cancelled.
    private static string OrderStatusOf(string orderId, string status, HashSet<string> failed) =>
        status == nameof(OrderStatus.Cancelled) && failed.Contains(orderId) ? nameof(OrderStatus.DeliveryFailed) : status;

    private static string OrderHistoryStatus(string orderId, string status, string? comment, HashSet<string> failed) =>
        status == nameof(OrderStatus.Cancelled) && failed.Contains(orderId) && comment?.StartsWith("Доставка не выполнена", StringComparison.Ordinal) == true
            ? nameof(OrderStatus.DeliveryFailed) : status;

    private static void Copy(SqliteConnection src, NpgsqlConnection dst, Dictionary<string, long> counts, string table, string select, string insert, Func<SqliteDataReader, object?[]> map)
    {
        long n = 0;
        using var r = Query(src, select);
        while (r.Read())
        {
            using var cmd = new NpgsqlCommand(insert, dst);
            var vals = map(r);
            for (int i = 0; i < vals.Length; i++) cmd.Parameters.AddWithValue(i.ToString(), vals[i] ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            n++;
        }
        counts[table] = n;
    }

    private static Guid G(SqliteDataReader r, int i) => Guid.Parse(r.GetString(i));
    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime T(SqliteDataReader r, int i) => DateTime.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static DateTime? TN(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : T(r, i);
    private static long Kopecks(SqliteDataReader r, int i) => Money.ToKopecks((decimal)r.GetDouble(i));

    private static SqliteDataReader Query(SqliteConnection c, string sql) { var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteReader(); }
    private static object? Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    private static long Count(NpgsqlConnection c, string sql) { using var cmd = new NpgsqlCommand(sql, c); return (long)cmd.ExecuteScalar()!; }
}
