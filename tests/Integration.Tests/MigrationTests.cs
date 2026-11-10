using ApiHost.Db;
using Npgsql;
using Xunit;

namespace Integration.Tests;

/// <summary>Версионированные миграции: данные не теряются, ошибки откатываются, чужую схему не трогаем.</summary>
public class MigrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly NpgsqlDataSource _ds;
    public MigrationTests() => _ds = NpgsqlDataSource.Create(_db.ConnectionString);
    public void Dispose() { _ds.Dispose(); _db.Dispose(); }

    private static readonly Guid Cust = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Order = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Deliv = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Cour = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>Данные, как их хранит версия 1 схемы: клиент, курьер, товар, заказ с позицией, доставка и история.</summary>
    private void InsertV1Data() => _db.Sql($@"
INSERT INTO customers(id,name,phone) VALUES('{Cust}','Иван','+7');
INSERT INTO customer_addresses(id,customer_id,city,street,house) VALUES(gen_random_uuid(),'{Cust}','Волгоград','Мира','1');
INSERT INTO couriers(id,name,phone,state) VALUES('{Cour}','Курьер','+7','Busy');
INSERT INTO products(id,name,price_kopecks) VALUES(gen_random_uuid(),'Пицца',59000);
INSERT INTO orders(id,customer_id,address,comment,status,created_at) VALUES('{Order}','{Cust}','г. Волгоград','коммент','Confirmed',now());
INSERT INTO order_items(order_id,product_id,name,price_kopecks,quantity) VALUES('{Order}',gen_random_uuid(),'Пицца',59000,2);
INSERT INTO deliveries(id,order_id,courier_id,status,created_at) VALUES('{Deliv}','{Order}','{Cour}','Assigned',now());
INSERT INTO status_history(delivery_id,status,at,comment) VALUES('{Deliv}','Created',now(),'создана'),('{Deliv}','Assigned',now(),NULL);
INSERT INTO order_history(order_id,status,at,comment) VALUES('{Order}','Created',now(),NULL),('{Order}','Confirmed',now(),NULL);");

    private long Rows(string table) => _db.Scalar<long>($"SELECT COUNT(*) FROM {table}");

    [Fact]
    public void FreshDatabase_AppliesAllMigrations_AndIsIdempotent()
    {
        Assert.Equal(Migrations.All.Count, Migrator.Apply(_ds, Migrations.All));
        Assert.Equal(Migrations.All.Count, Migrator.Apply(_ds, Migrations.All)); // повторный запуск ничего не меняет
        Assert.Equal(Migrations.All.Count, (int)Rows("schema_migrations"));
    }

    [Fact]
    public void Upgrade_FromV1_KeepsAllExistingRecords()
    {
        Migrator.Apply(_ds, Migrations.All, upTo: 1);
        InsertV1Data();
        var before = new[] { "customers", "customer_addresses", "couriers", "products", "orders", "order_items", "deliveries", "status_history", "order_history" }
            .ToDictionary(t => t, Rows);

        Migrator.Apply(_ds, Migrations.All);

        foreach (var (t, n) in before) Assert.Equal(n, Rows(t));
        Assert.Equal(118000L, _db.Scalar<long>("SELECT SUM(price_kopecks*quantity)::bigint FROM order_items"));
        Assert.Equal("Assigned", _db.Scalar<string>("SELECT status FROM deliveries"));
        Assert.Equal("коммент", _db.Scalar<string>("SELECT comment FROM orders"));
    }

    [Fact]
    public void FailingMigration_IsRolledBack_AndKeepsPreviousVersionAndData()
    {
        Migrator.Apply(_ds, Migrations.All.Take(1).ToList());
        InsertV1Data();
        var broken = Migrations.All.Take(1).Append(new Migration(2, "broken",
            "CREATE TABLE half_done(a int); INSERT INTO no_such_table VALUES(1);")).ToList();

        var ex = Assert.Throws<MigrationException>(() => Migrator.Apply(_ds, broken));

        Assert.Contains("0002", ex.Message);
        Assert.Equal(1, _db.Scalar<int>("SELECT MAX(version) FROM schema_migrations"));
        Assert.False(_db.Scalar<bool>("SELECT to_regclass('public.half_done') IS NOT NULL")); // CREATE TABLE тоже откатился
        Assert.Equal(1, Rows("orders"));
        Assert.Equal(1, Rows("deliveries"));
    }

    [Fact]
    public void ExistingTablesWithoutVersionRecord_AreNotTouched()
    {
        _db.Sql("CREATE TABLE customers(id int); INSERT INTO customers VALUES (1);");
        Assert.Throws<MigrationException>(() => Migrator.Apply(_ds, Migrations.All));
        Assert.Equal(1, Rows("customers"));
    }

    [Fact]
    public void DatabaseFromNewerApp_IsRejected()
    {
        Migrator.Apply(_ds, Migrations.All);
        _db.Sql("INSERT INTO schema_migrations(version,name) VALUES (999,'from_future')");
        var ex = Assert.Throws<MigrationException>(() => Migrator.Apply(_ds, Migrations.All));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public async Task ConcurrentStarts_DoNotConflict()
    {
        var versions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Migrator.Apply(_ds, Migrations.All))));
        Assert.All(versions, v => Assert.Equal(Migrations.All.Count, v));
    }

    [Fact]
    public void MigrationVersions_MustBeSequential() =>
        Assert.Throws<MigrationException>(() => Migrator.Apply(_ds, new[] { new Migration(1, "a", "SELECT 1"), new Migration(3, "c", "SELECT 1") }));

    [Fact]
    public void UnreachableServer_GivesMigrationException()
    {
        using var bad = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Timeout=2");
        Assert.Throws<MigrationException>(() => Migrator.Apply(bad, Migrations.All));
    }

    [Fact]
    public void Store_Restart_KeepsDataAndDoesNotReseed()
    {
        Guid orderId;
        using (var s1 = _db.OpenStore())
        {
            var ivan = s1.Customers().First(c => c.Name.StartsWith("Иван")).Id;
            var pizza = s1.Products().First(p => p.Name.Contains("Пицца"));
            var (d, _, e) = s1.CreateOrder(new(ivan, new() { new(pizza.Id, 1) }, new("Волгоград", "Мира", "1")));
            Assert.Null(e);
            orderId = d!.OrderId;
        }
        using var s2 = _db.OpenStore(); // «перезапуск приложения»
        Assert.NotNull(s2.Order(orderId));
        Assert.Equal(2, s2.Customers().Count);
        Assert.Equal(8, s2.Products().Count);
        Assert.Equal(3, s2.Couriers().Count);
    }
}
