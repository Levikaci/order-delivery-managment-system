using System.Security.Cryptography;
using Contracts;
using Microsoft.Data.Sqlite;
using Npgsql;
using SqliteImport;
using Xunit;

namespace Integration.Tests;

/// <summary>Перенос существующей установки (SQLite v2) в PostgreSQL без потери данных.</summary>
public class SqliteImportTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}.db");
    private static readonly Guid Cust = Guid.NewGuid(), Cour = Guid.NewGuid(), Prod = Guid.NewGuid();
    private static readonly Guid OrdOk = Guid.NewGuid(), OrdFail = Guid.NewGuid(), OrdCancel = Guid.NewGuid();
    private static readonly Guid DelOk = Guid.NewGuid(), DelFail = Guid.NewGuid(), DelCancel = Guid.NewGuid();
    private const string Ts = "2025-03-01T10:00:00.0000000Z";

    public void Dispose() { _db.Dispose(); foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + "*")) try { File.Delete(f); } catch { } }

    private void Exec(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Файл в формате прежней версии приложения (схема v2, цены REAL, статусы как раньше).</summary>
    private void BuildOldDb(int version = 2)
    {
        using var c = new SqliteConnection($"Data Source={_file};Pooling=False"); c.Open();
        Exec(c, @"
CREATE TABLE customers(id TEXT PRIMARY KEY, name TEXT NOT NULL, phone TEXT NOT NULL DEFAULT '');
CREATE TABLE customer_addresses(id TEXT PRIMARY KEY, customer_id TEXT NOT NULL, label TEXT NOT NULL DEFAULT '', city TEXT NOT NULL, street TEXT NOT NULL, house TEXT NOT NULL, apartment TEXT NULL);
CREATE TABLE couriers(id TEXT PRIMARY KEY, name TEXT NOT NULL, phone TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'Available');
CREATE TABLE products(id TEXT PRIMARY KEY, name TEXT NOT NULL, price REAL NOT NULL);
CREATE TABLE users(id TEXT PRIMARY KEY, login TEXT NOT NULL UNIQUE COLLATE NOCASE, salt BLOB NOT NULL, hash BLOB NOT NULL, role TEXT NOT NULL, ref_id TEXT NULL);
CREATE TABLE orders(id TEXT PRIMARY KEY, customer_id TEXT NOT NULL, address TEXT NOT NULL, comment TEXT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE order_items(id INTEGER PRIMARY KEY AUTOINCREMENT, order_id TEXT NOT NULL, product_id TEXT NOT NULL, name TEXT NOT NULL, price REAL NOT NULL, quantity INTEGER NOT NULL);
CREATE TABLE deliveries(id TEXT PRIMARY KEY, order_id TEXT NOT NULL UNIQUE, courier_id TEXT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL, started_at TEXT NULL, finished_at TEXT NULL, failure_reason TEXT NULL, confirmed_at TEXT NULL);
CREATE TABLE status_history(id INTEGER PRIMARY KEY AUTOINCREMENT, delivery_id TEXT NOT NULL, status TEXT NOT NULL, at TEXT NOT NULL, comment TEXT NULL);
CREATE TABLE order_history(id INTEGER PRIMARY KEY AUTOINCREMENT, order_id TEXT NOT NULL, status TEXT NOT NULL, at TEXT NOT NULL, comment TEXT NULL);");
        Exec(c, $"PRAGMA user_version={version}");
        Exec(c, "INSERT INTO customers VALUES($i,'Иван','+7 900')", ("$i", Cust.ToString()));
        Exec(c, "INSERT INTO customer_addresses VALUES($i,$c,'Дом','Волгоград','Мира','15','24')", ("$i", Guid.NewGuid().ToString()), ("$c", Cust.ToString()));
        Exec(c, "INSERT INTO couriers VALUES($i,'Алексей','+7','Busy')", ("$i", Cour.ToString()));
        Exec(c, "INSERT INTO products VALUES($i,'Кофе',19.99)", ("$i", Prod.ToString()));
        var salt = RandomNumberGenerator.GetBytes(16);
        Exec(c, "INSERT INTO users VALUES($i,'Ivan',$s,$h,'client',$r)", ("$i", Guid.NewGuid().ToString()), ("$s", salt),
            ("$h", Rfc2898DeriveBytes.Pbkdf2("secret", salt, 100_000, HashAlgorithmName.SHA256, 32)), ("$r", Cust.ToString()));
        foreach (var (o, d, os, ds, reason) in new[] {
            (OrdOk, DelOk, "Delivered", "Delivered", (string?)null),
            (OrdFail, DelFail, "Cancelled", "Failed", "Клиент не отвечает"),   // как хранилось раньше: неудача = Cancelled
            (OrdCancel, DelCancel, "Cancelled", "Cancelled", null) })
        {
            Exec(c, "INSERT INTO orders VALUES($o,$c,'г. Волгоград',NULL,$s,$t)", ("$o", o.ToString()), ("$c", Cust.ToString()), ("$s", os), ("$t", Ts));
            Exec(c, "INSERT INTO order_items(order_id,product_id,name,price,quantity) VALUES($o,$p,'Кофе',19.99,3)", ("$o", o.ToString()), ("$p", Prod.ToString()));
            Exec(c, "INSERT INTO deliveries VALUES($d,$o,$cr,$s,$t,NULL,$t,$f,NULL)", ("$d", d.ToString()), ("$o", o.ToString()), ("$cr", Cour.ToString()), ("$s", ds), ("$t", Ts), ("$f", reason));
            Exec(c, "INSERT INTO status_history(delivery_id,status,at,comment) VALUES($d,'Created',$t,'Доставка создана'),($d,$s,$t,$f)", ("$d", d.ToString()), ("$s", ds), ("$t", Ts), ("$f", reason));
            Exec(c, "INSERT INTO order_history(order_id,status,at,comment) VALUES($o,'Created',$t,'Заказ создан'),($o,$s,$t,$f)", ("$o", o.ToString()), ("$s", os), ("$t", Ts),
                ("$f", ds == "Failed" ? "Доставка не выполнена: " + reason : null));
        }
    }

    private IReadOnlyDictionary<string, long> Import()
    {
        using var ds = NpgsqlDataSource.Create(_db.ConnectionString);
        return SqliteImporter.Run(_file, ds);
    }

    [Fact]
    public void Import_CopiesEverything_ConvertsMoney_AndFixesFailedStatus()
    {
        BuildOldDb();
        var counts = Import();

        Assert.Equal(1, counts["customers"]);
        Assert.Equal(3, counts["orders"]);
        Assert.Equal(3, counts["deliveries"]);
        Assert.Equal(6, counts["status_history"]);
        Assert.Equal(1999L, _db.Scalar<long>("SELECT price_kopecks FROM products"));
        Assert.Equal(3 * 1999L, _db.Scalar<long>("SELECT SUM(price_kopecks*quantity)::bigint FROM order_items WHERE order_id='" + OrdOk + "'"));
        Assert.Equal("DeliveryFailed", _db.Scalar<string>($"SELECT status FROM orders WHERE id='{OrdFail}'"));
        Assert.Equal("Cancelled", _db.Scalar<string>($"SELECT status FROM orders WHERE id='{OrdCancel}'"));       // настоящая отмена не тронута
        Assert.Equal("DeliveryFailed", _db.Scalar<string>($"SELECT status FROM order_history WHERE order_id='{OrdFail}' ORDER BY id DESC LIMIT 1"));
        Assert.Equal("Cancelled", _db.Scalar<string>($"SELECT status FROM order_history WHERE order_id='{OrdCancel}' ORDER BY id DESC LIMIT 1"));
        Assert.Equal("Клиент не отвечает", _db.Scalar<string>($"SELECT failure_reason FROM deliveries WHERE id='{DelFail}'"));
    }

    [Fact]
    public void ImportedDatabase_WorksWithStore_LoginAndTotals()
    {
        BuildOldDb();
        Import();
        using var s = _db.OpenStore();                   // миграции уже применены, демо-данные не добавляются
        Assert.Single(s.Customers());
        Assert.Equal("Иван", s.Authenticate("ivan", "secret")!.Name);   // хэш пароля перенесён как есть
        var d = s.Order(OrdOk)!;
        Assert.Equal(3 * 19.99m, d.Total);
        Assert.Equal(OrderStatus.DeliveryFailed, s.Order(OrdFail)!.OrderStatus);
    }

    [Fact]
    public void Import_IntoNonEmptyDatabase_IsRefused_AndSourceUntouched()
    {
        BuildOldDb();
        Import();
        var before = File.ReadAllBytes(_file);
        Assert.Throws<ImportException>(Import);
        Assert.Equal(before, File.ReadAllBytes(_file));
        Assert.Equal(1, _db.Scalar<long>("SELECT COUNT(*) FROM customers"));
    }

    [Fact]
    public void Import_UnsupportedVersion_OrMissingFile_IsRefused()
    {
        BuildOldDb(version: 1);
        Assert.Contains("v1", Assert.Throws<ImportException>(Import).Message);
        File.Delete(_file);
        Assert.Throws<ImportException>(Import);
    }
}
