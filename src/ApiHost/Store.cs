using System.Globalization;
using System.Security.Cryptography;
using CourierModule;
using CustomerModule;
using DeliveryModule;
using Microsoft.Data.Sqlite;
using OrderModule;

namespace ApiHost;

/// <summary>
/// Хранилище на SQLite и сценарии работы системы. Бизнес-правила берутся из модулей
/// (DeliveryRules, OrderRules, CourierRules, CustomerRules), здесь они применяются к данным.
/// </summary>
public class Store
{
    private const int SchemaVersion = 2;
    private readonly string _cs;
    private readonly object _lock = new();
    private readonly ILogger<Store> _log;

    public Store(IConfiguration cfg, ILogger<Store> log)
    {
        _log = log;
        _cs = cfg.GetConnectionString("Default") ?? "Data Source=data/delivery.db";
        var path = new SqliteConnectionStringBuilder(_cs).DataSource;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Init();
    }

    // ---------- инфраструктура ----------
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Cmd(c, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;").ExecuteNonQuery();
        return c;
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private static long Count(SqliteConnection c, string sql, params (string, object?)[] ps) => (long)Cmd(c, sql, ps).ExecuteScalar()!;
    private static string Now() => DateTime.UtcNow.ToString("o");
    private static DateTime Dt(string s) => DateTime.Parse(s, null, DateTimeStyles.RoundtripKind);
    private static DateTime? DtN(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Dt(r.GetString(i));
    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private void Init()
    {
        using var c = Open();
        var ver = (long)Cmd(c, "PRAGMA user_version").ExecuteScalar()!;
        if (ver < SchemaVersion)
        {
            // Схема v1 (до доработки по ТЗ) несовместима: пересоздаём таблицы.
            Cmd(c, @"PRAGMA foreign_keys=OFF;
DROP TABLE IF EXISTS order_history; DROP TABLE IF EXISTS status_history; DROP TABLE IF EXISTS order_items;
DROP TABLE IF EXISTS deliveries; DROP TABLE IF EXISTS orders; DROP TABLE IF EXISTS customer_addresses;
DROP TABLE IF EXISTS users; DROP TABLE IF EXISTS products; DROP TABLE IF EXISTS couriers; DROP TABLE IF EXISTS customers;
PRAGMA foreign_keys=ON;").ExecuteNonQuery();
        }
        Cmd(c, @"
CREATE TABLE IF NOT EXISTS customers(id TEXT PRIMARY KEY, name TEXT NOT NULL, phone TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS customer_addresses(
  id TEXT PRIMARY KEY, customer_id TEXT NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
  label TEXT NOT NULL DEFAULT '', city TEXT NOT NULL, street TEXT NOT NULL, house TEXT NOT NULL, apartment TEXT NULL);
CREATE TABLE IF NOT EXISTS couriers(id TEXT PRIMARY KEY, name TEXT NOT NULL, phone TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'Available');
CREATE TABLE IF NOT EXISTS products(id TEXT PRIMARY KEY, name TEXT NOT NULL, price REAL NOT NULL);
CREATE TABLE IF NOT EXISTS users(
  id TEXT PRIMARY KEY, login TEXT NOT NULL UNIQUE COLLATE NOCASE, salt BLOB NOT NULL, hash BLOB NOT NULL,
  role TEXT NOT NULL, ref_id TEXT NULL);
CREATE TABLE IF NOT EXISTS orders(
  id TEXT PRIMARY KEY, customer_id TEXT NOT NULL REFERENCES customers(id),
  address TEXT NOT NULL, comment TEXT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS order_items(
  id INTEGER PRIMARY KEY AUTOINCREMENT, order_id TEXT NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
  product_id TEXT NOT NULL, name TEXT NOT NULL, price REAL NOT NULL, quantity INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS deliveries(
  id TEXT PRIMARY KEY, order_id TEXT NOT NULL UNIQUE REFERENCES orders(id),
  courier_id TEXT NULL REFERENCES couriers(id), status TEXT NOT NULL, created_at TEXT NOT NULL,
  started_at TEXT NULL, finished_at TEXT NULL, failure_reason TEXT NULL, confirmed_at TEXT NULL);
CREATE TABLE IF NOT EXISTS status_history(
  id INTEGER PRIMARY KEY AUTOINCREMENT, delivery_id TEXT NOT NULL REFERENCES deliveries(id),
  status TEXT NOT NULL, at TEXT NOT NULL, comment TEXT NULL);
CREATE TABLE IF NOT EXISTS order_history(
  id INTEGER PRIMARY KEY AUTOINCREMENT, order_id TEXT NOT NULL REFERENCES orders(id),
  status TEXT NOT NULL, at TEXT NOT NULL, comment TEXT NULL);
CREATE INDEX IF NOT EXISTS ix_deliveries_status ON deliveries(status);
CREATE INDEX IF NOT EXISTS ix_deliveries_courier ON deliveries(courier_id);
CREATE INDEX IF NOT EXISTS ix_orders_customer ON orders(customer_id);
CREATE INDEX IF NOT EXISTS ix_history_delivery ON status_history(delivery_id);
CREATE INDEX IF NOT EXISTS ix_ohistory_order ON order_history(order_id);").ExecuteNonQuery();
        Cmd(c, $"PRAGMA user_version={SchemaVersion}").ExecuteNonQuery();

        if (Count(c, "SELECT COUNT(*) FROM users") == 0) Seed(c);
    }

    private void Seed(SqliteConnection c)
    {
        AddUser(c, "operator", "operator", "operator", null);
        var ivan = AddCustomer(c, "Иван Петров", "+7 900 111-22-33");
        var maria = AddCustomer(c, "Мария Сидорова", "+7 900 444-55-66");
        AddUser(c, "ivan", "ivan", "client", ivan);
        AddUser(c, "maria", "maria", "client", maria);
        AddAddr(c, ivan, "Дом", "Волгоград", "Мира", "15", "24");
        AddAddr(c, maria, "Дом", "Волгоград", "Чуйкова", "3", "7");
        foreach (var (n, p, l) in new[] { ("Алексей Курьеров", "+7 900 200-00-01", "alexey"), ("Дмитрий Быстров", "+7 900 200-00-02", "dmitry"), ("Ольга Скорая", "+7 900 200-00-03", "olga") })
        {
            var id = AddCourier(c, n, p);
            AddUser(c, l, l, "courier", id);
        }
        foreach (var (n, p) in new[] { ("Пицца Маргарита", 590m), ("Бургер классический", 420m), ("Картофель фри", 190m), ("Сок апельсиновый", 150m), ("Суши-сет", 1290m), ("Букет роз", 2500m), ("Книга", 800m), ("Кофе", 220m) })
            AddProduct(c, n, p);
        _log.LogInformation("База данных создана, добавлены тестовые данные");
    }

    // ---------- пользователи и вход ----------
    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

    private static void AddUser(SqliteConnection c, string login, string password, string role, Guid? refId)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        Cmd(c, "INSERT INTO users(id,login,salt,hash,role,ref_id) VALUES($i,$l,$s,$h,$r,$f)",
            ("$i", Guid.NewGuid().ToString()), ("$l", login), ("$s", salt), ("$h", Hash(password, salt)), ("$r", role), ("$f", refId?.ToString())).ExecuteNonQuery();
    }

    public Session? Authenticate(string? login, string? password)
    {
        if (string.IsNullOrWhiteSpace(login) || password is null) return null;
        using var c = Open();
        using var r = Cmd(c, "SELECT salt,hash,role,ref_id FROM users WHERE login=$l", ("$l", login.Trim())).ExecuteReader();
        if (!r.Read()) return null;
        var salt = (byte[])r["salt"]; var hash = (byte[])r["hash"];
        if (!CryptographicOperations.FixedTimeEquals(hash, Hash(password, salt))) return null;
        var role = r.GetString(2);
        Guid? refId = r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3));
        r.Close();
        var name = role switch
        {
            "client" => Cmd(c, "SELECT name FROM customers WHERE id=$i", ("$i", refId.ToString())).ExecuteScalar() as string,
            "courier" => Cmd(c, "SELECT name FROM couriers WHERE id=$i", ("$i", refId.ToString())).ExecuteScalar() as string,
            _ => "Оператор"
        };
        return new Session(role, refId, name ?? login);
    }

    private static (string login, string password) NewCredentials(SqliteConnection c, string? login, string? password, string prefix)
    {
        login = string.IsNullOrWhiteSpace(login) ? $"{prefix}{RandomNumberGenerator.GetInt32(1000, 9999)}" : login.Trim();
        password = string.IsNullOrWhiteSpace(password) ? RandomNumberGenerator.GetInt32(100000, 999999).ToString() : password;
        return (login, password);
    }

    // ---------- клиенты ----------
    private static Guid AddCustomer(SqliteConnection c, string name, string? phone)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO customers(id,name,phone) VALUES($i,$n,$p)", ("$i", id.ToString()), ("$n", name), ("$p", phone ?? "")).ExecuteNonQuery();
        return id;
    }

    public (object? created, string? error) NewCustomer(NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return (null, e);
        lock (_lock)
        {
            using var c = Open();
            var (login, pwd) = NewCredentials(c, p.Login, p.Password, "client");
            if (Count(c, "SELECT COUNT(*) FROM users WHERE login=$l", ("$l", login)) > 0) return (null, "LOGIN_TAKEN");
            using var tx = c.BeginTransaction();
            var id = AddCustomer(c, p.Name.Trim(), p.Phone);
            AddUser(c, login, pwd, "client", id);
            tx.Commit();
            return (new { id, name = p.Name.Trim(), phone = p.Phone ?? "", login, password = pwd }, null);
        }
    }

    public string? UpdateCustomer(Guid id, NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return e;
        lock (_lock)
        {
            using var c = Open();
            return Cmd(c, "UPDATE customers SET name=$n,phone=$p WHERE id=$i", ("$n", p.Name.Trim()), ("$p", p.Phone ?? ""), ("$i", id.ToString())).ExecuteNonQuery() == 0 ? "CUSTOMER_NOT_FOUND" : null;
        }
    }

    public string? DeleteCustomer(Guid id)
    {
        lock (_lock)
        {
            using var c = Open();
            if (Count(c, "SELECT COUNT(*) FROM orders WHERE customer_id=$i", ("$i", id.ToString())) > 0) return "CUSTOMER_HAS_ORDERS";
            using var tx = c.BeginTransaction();
            Cmd(c, "DELETE FROM users WHERE ref_id=$i AND role='client'", ("$i", id.ToString())).ExecuteNonQuery();
            var n = Cmd(c, "DELETE FROM customers WHERE id=$i", ("$i", id.ToString())).ExecuteNonQuery();
            tx.Commit();
            return n == 0 ? "CUSTOMER_NOT_FOUND" : null;
        }
    }

    public List<Customer> Customers() { using var c = Open(); return Customers(c); }
    private static List<Customer> Customers(SqliteConnection c)
    {
        using var r = Cmd(c, "SELECT id,name,phone FROM customers ORDER BY name").ExecuteReader();
        var l = new List<Customer>();
        while (r.Read()) l.Add(new Customer(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2)));
        return l;
    }
    public Customer? Customer(Guid id) => Customers().FirstOrDefault(x => x.Id == id);

    // ---------- адреса клиента ----------
    private static Guid AddAddr(SqliteConnection c, Guid customerId, string label, string city, string street, string house, string? apt)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO customer_addresses(id,customer_id,label,city,street,house,apartment) VALUES($i,$c,$l,$ci,$s,$h,$a)",
            ("$i", id.ToString()), ("$c", customerId.ToString()), ("$l", label), ("$ci", city.Trim()), ("$s", street.Trim()), ("$h", house.Trim()),
            ("$a", string.IsNullOrWhiteSpace(apt) ? null : apt.Trim())).ExecuteNonQuery();
        return id;
    }

    private static List<SavedAddress> Addresses(SqliteConnection c, Guid? customerId = null)
    {
        using var r = Cmd(c, "SELECT id,customer_id,label,city,street,house,apartment FROM customer_addresses" + (customerId is null ? "" : " WHERE customer_id=$c") + " ORDER BY label,street",
            customerId is null ? Array.Empty<(string, object?)>() : new (string, object?)[] { ("$c", customerId.ToString()) }).ExecuteReader();
        var l = new List<SavedAddress>();
        while (r.Read()) l.Add(new SavedAddress(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Str(r, 6)));
        return l;
    }

    public List<SavedAddress> Addresses(Guid customerId) { using var c = Open(); return Addresses(c, customerId); }
    public SavedAddress? Address(Guid id) { using var c = Open(); return Addresses(c).FirstOrDefault(a => a.Id == id); }

    public (SavedAddress? a, string? error) AddAddress(Guid customerId, AddressRequest r)
    {
        if (CustomerRules.ValidateAddress(r.City, r.Street, r.House) is { } e) return (null, e);
        lock (_lock)
        {
            using var c = Open();
            if (Count(c, "SELECT COUNT(*) FROM customers WHERE id=$i", ("$i", customerId.ToString())) == 0) return (null, "CUSTOMER_NOT_FOUND");
            var id = AddAddr(c, customerId, r.Label?.Trim() ?? "", r.City, r.Street, r.House, r.Apartment);
            return (Addresses(c, customerId).First(a => a.Id == id), null);
        }
    }

    public string? UpdateAddress(Guid id, AddressRequest r)
    {
        if (CustomerRules.ValidateAddress(r.City, r.Street, r.House) is { } e) return e;
        lock (_lock)
        {
            using var c = Open();
            return Cmd(c, "UPDATE customer_addresses SET label=$l,city=$c,street=$s,house=$h,apartment=$a WHERE id=$i",
                ("$l", r.Label?.Trim() ?? ""), ("$c", r.City.Trim()), ("$s", r.Street.Trim()), ("$h", r.House.Trim()),
                ("$a", string.IsNullOrWhiteSpace(r.Apartment) ? null : r.Apartment.Trim()), ("$i", id.ToString())).ExecuteNonQuery() == 0 ? "ADDRESS_NOT_FOUND" : null;
        }
    }

    public string? DeleteAddress(Guid id)
    {
        lock (_lock)
        {
            using var c = Open();
            return Cmd(c, "DELETE FROM customer_addresses WHERE id=$i", ("$i", id.ToString())).ExecuteNonQuery() == 0 ? "ADDRESS_NOT_FOUND" : null;
        }
    }

    // ---------- курьеры ----------
    private static Guid AddCourier(SqliteConnection c, string name, string? phone)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO couriers(id,name,phone,state) VALUES($i,$n,$p,'Available')", ("$i", id.ToString()), ("$n", name), ("$p", phone ?? "")).ExecuteNonQuery();
        return id;
    }

    public (object? created, string? error) NewCourier(NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return (null, e);
        lock (_lock)
        {
            using var c = Open();
            var (login, pwd) = NewCredentials(c, p.Login, p.Password, "courier");
            if (Count(c, "SELECT COUNT(*) FROM users WHERE login=$l", ("$l", login)) > 0) return (null, "LOGIN_TAKEN");
            using var tx = c.BeginTransaction();
            var id = AddCourier(c, p.Name.Trim(), p.Phone);
            AddUser(c, login, pwd, "courier", id);
            tx.Commit();
            return (new { id, name = p.Name.Trim(), phone = p.Phone ?? "", state = CourierState.Available, available = true, login, password = pwd }, null);
        }
    }

    public string? UpdateCourier(Guid id, NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return e;
        lock (_lock)
        {
            using var c = Open();
            return Cmd(c, "UPDATE couriers SET name=$n,phone=$p WHERE id=$i", ("$n", p.Name.Trim()), ("$p", p.Phone ?? ""), ("$i", id.ToString())).ExecuteNonQuery() == 0 ? "COURIER_NOT_FOUND" : null;
        }
    }

    public string? DeleteCourier(Guid id)
    {
        lock (_lock)
        {
            using var c = Open();
            if (Count(c, "SELECT COUNT(*) FROM deliveries WHERE courier_id=$i", ("$i", id.ToString())) > 0) return "COURIER_HAS_ORDERS";
            using var tx = c.BeginTransaction();
            Cmd(c, "DELETE FROM users WHERE ref_id=$i AND role='courier'", ("$i", id.ToString())).ExecuteNonQuery();
            var n = Cmd(c, "DELETE FROM couriers WHERE id=$i", ("$i", id.ToString())).ExecuteNonQuery();
            tx.Commit();
            return n == 0 ? "COURIER_NOT_FOUND" : null;
        }
    }

    public List<Courier> Couriers(bool onlyAvailable = false) { using var c = Open(); return Couriers(c, onlyAvailable); }
    private static List<Courier> Couriers(SqliteConnection c, bool onlyAvailable = false)
    {
        using var r = Cmd(c, "SELECT id,name,phone,state FROM couriers" + (onlyAvailable ? " WHERE state='Available'" : "") + " ORDER BY name").ExecuteReader();
        var l = new List<Courier>();
        while (r.Read()) l.Add(new Courier(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), Enum.Parse<CourierState>(r.GetString(3))));
        return l;
    }

    public string? SetCourierState(Guid id, CourierState state, bool byOperator)
    {
        lock (_lock)
        {
            using var c = Open();
            var cur = Couriers(c).FirstOrDefault(x => x.Id == id);
            if (cur is null) return "COURIER_NOT_FOUND";
            if (CourierRules.ValidateStateChange(cur.State, state, byOperator) is { } e) return e;
            Cmd(c, "UPDATE couriers SET state=$s WHERE id=$i", ("$s", state.ToString()), ("$i", id.ToString())).ExecuteNonQuery();
            _log.LogInformation("Курьер {Courier}: состояние {From} → {To}", id, cur.State, state);
            return null;
        }
    }

    // ---------- товары ----------
    private static Guid AddProduct(SqliteConnection c, string name, decimal price)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO products(id,name,price) VALUES($i,$n,$p)", ("$i", id.ToString()), ("$n", name), ("$p", (double)price)).ExecuteNonQuery();
        return id;
    }

    public List<Product> Products() { using var c = Open(); return Products(c); }
    private static List<Product> Products(SqliteConnection c)
    {
        using var r = Cmd(c, "SELECT id,name,price FROM products ORDER BY name").ExecuteReader();
        var l = new List<Product>();
        while (r.Read()) l.Add(new Product(Guid.Parse(r.GetString(0)), r.GetString(1), (decimal)r.GetDouble(2)));
        return l;
    }

    public (Product? p, string? error) NewProduct(ProductRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Price < 0 || r.Price > 1_000_000) return (null, "INVALID_PRODUCT");
        lock (_lock) { using var c = Open(); var id = AddProduct(c, r.Name.Trim(), r.Price); return (new Product(id, r.Name.Trim(), r.Price), null); }
    }

    public string? UpdateProduct(Guid id, ProductRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Price < 0 || r.Price > 1_000_000) return "INVALID_PRODUCT";
        lock (_lock)
        {
            using var c = Open();
            return Cmd(c, "UPDATE products SET name=$n,price=$p WHERE id=$i", ("$n", r.Name.Trim()), ("$p", (double)r.Price), ("$i", id.ToString())).ExecuteNonQuery() == 0 ? "PRODUCT_NOT_FOUND" : null;
        }
    }

    public string? DeleteProduct(Guid id)
    {
        lock (_lock)
        {
            using var c = Open();
            return Cmd(c, "DELETE FROM products WHERE id=$i", ("$i", id.ToString())).ExecuteNonQuery() == 0 ? "PRODUCT_NOT_FOUND" : null;
        }
    }

    // ---------- чтение заказов и доставок ----------
    public List<Delivery> Deliveries(string? q = null, DeliveryStatus? status = null, Guid? courierId = null, Guid? customerId = null)
    {
        using var c = Open();
        return Deliveries(c, q, status, courierId, customerId);
    }

    private static List<Delivery> Deliveries(SqliteConnection c, string? q = null, DeliveryStatus? status = null, Guid? courierId = null, Guid? customerId = null, Guid? deliveryId = null, Guid? orderId = null)
    {
        var sql = @"SELECT d.id,d.order_id,o.customer_id,d.courier_id,o.address,o.comment,d.status,o.status,o.created_at,
                           d.started_at,d.finished_at,d.failure_reason,d.confirmed_at
                    FROM deliveries d JOIN orders o ON o.id=d.order_id WHERE 1=1";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            sql += @" AND (o.address LIKE $q OR o.comment LIKE $q OR d.id LIKE $q OR o.id LIKE $q
                       OR EXISTS(SELECT 1 FROM order_items i WHERE i.order_id=o.id AND i.name LIKE $q))";
            ps.Add(("$q", "%" + q.Trim() + "%"));
        }
        if (status is not null) { sql += " AND d.status=$s"; ps.Add(("$s", status.ToString())); }
        if (courierId is not null) { sql += " AND d.courier_id=$c"; ps.Add(("$c", courierId.ToString())); }
        if (customerId is not null) { sql += " AND o.customer_id=$u"; ps.Add(("$u", customerId.ToString())); }
        if (deliveryId is not null) { sql += " AND d.id=$d"; ps.Add(("$d", deliveryId.ToString())); }
        if (orderId is not null) { sql += " AND d.order_id=$o"; ps.Add(("$o", orderId.ToString())); }
        sql += " ORDER BY o.created_at DESC";
        var list = new List<Delivery>();
        using (var r = Cmd(c, sql, ps.ToArray()).ExecuteReader())
            while (r.Read())
                list.Add(new Delivery
                {
                    DeliveryId = Guid.Parse(r.GetString(0)),
                    OrderId = Guid.Parse(r.GetString(1)),
                    CustomerId = Guid.Parse(r.GetString(2)),
                    CourierId = r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)),
                    Address = r.GetString(4),
                    Comment = Str(r, 5),
                    Status = Enum.Parse<DeliveryStatus>(r.GetString(6)),
                    OrderStatus = Enum.Parse<OrderStatus>(r.GetString(7)),
                    CreatedAt = Dt(r.GetString(8)),
                    StartedAt = DtN(r, 9),
                    FinishedAt = DtN(r, 10),
                    FailureReason = Str(r, 11),
                    ConfirmedAt = DtN(r, 12)
                });
        foreach (var d in list)
        {
            using (var i = Cmd(c, "SELECT product_id,name,price,quantity FROM order_items WHERE order_id=$o ORDER BY id", ("$o", d.OrderId.ToString())).ExecuteReader())
                while (i.Read()) d.Items.Add(new OrderItemDto(Guid.Parse(i.GetString(0)), i.GetString(1), (decimal)i.GetDouble(2), i.GetInt32(3)));
            d.Description = string.Join(", ", d.Items.Select(x => $"{x.Name} ×{x.Quantity}"));
            d.Total = OrderRules.Total(d.Items.Select(x => (x.Price, x.Quantity)));
            using (var h = Cmd(c, "SELECT status,at,comment FROM status_history WHERE delivery_id=$d ORDER BY id", ("$d", d.DeliveryId.ToString())).ExecuteReader())
                while (h.Read()) d.History.Add(new StatusChange(Enum.Parse<DeliveryStatus>(h.GetString(0)), Dt(h.GetString(1)), Str(h, 2)));
            using (var h = Cmd(c, "SELECT status,at,comment FROM order_history WHERE order_id=$o ORDER BY id", ("$o", d.OrderId.ToString())).ExecuteReader())
                while (h.Read()) d.OrderHistory.Add(new OrderStatusChange(Enum.Parse<OrderStatus>(h.GetString(0)), Dt(h.GetString(1)), Str(h, 2)));
        }
        return list;
    }

    public Delivery? Delivery(Guid id) { using var c = Open(); return Deliveries(c, deliveryId: id).FirstOrDefault(); }
    public Delivery? Order(Guid id) { using var c = Open(); return Deliveries(c, orderId: id).FirstOrDefault(); }

    private static void Hist(SqliteConnection c, Guid deliveryId, DeliveryStatus s, string? comment) =>
        Cmd(c, "INSERT INTO status_history(delivery_id,status,at,comment) VALUES($d,$s,$a,$m)",
            ("$d", deliveryId.ToString()), ("$s", s.ToString()), ("$a", Now()), ("$m", comment)).ExecuteNonQuery();

    private static void OrderHist(SqliteConnection c, Guid orderId, OrderStatus s, string? comment) =>
        Cmd(c, "INSERT INTO order_history(order_id,status,at,comment) VALUES($o,$s,$a,$m)",
            ("$o", orderId.ToString()), ("$s", s.ToString()), ("$a", Now()), ("$m", comment)).ExecuteNonQuery();

    // ---------- создание и изменение заказа ----------
    private static (List<(Product p, int q)>? lines, string? error) ResolveItems(SqliteConnection c, List<ItemRequest>? items)
    {
        if (OrderRules.ValidateQuantities(items?.Select(i => i.Quantity).ToList()) is { } e) return (null, e);
        var products = Products(c).ToDictionary(p => p.Id);
        var merged = new Dictionary<Guid, int>();
        foreach (var i in items!) merged[i.ProductId] = merged.GetValueOrDefault(i.ProductId) + i.Quantity;
        var lines = new List<(Product, int)>();
        foreach (var (id, q) in merged)
        {
            if (!products.TryGetValue(id, out var p)) return (null, "PRODUCT_NOT_FOUND");
            if (q > OrderRules.MaxQuantity) return (null, "INVALID_QUANTITY");
            lines.Add((p, q));
        }
        return (lines, null);
    }

    private static (string? text, AddressDto? inline, string? error) ResolveAddress(SqliteConnection c, Guid customerId, Guid? addressId, AddressDto? inline)
    {
        if (addressId is Guid aid)
        {
            var a = Addresses(c, customerId).FirstOrDefault(x => x.Id == aid);
            return a is null ? (null, null, "ADDRESS_NOT_FOUND") : (a.Text, null, null);
        }
        if (inline is null || CustomerRules.ValidateAddress(inline.City, inline.Street, inline.House) is { } e) return (null, null, "INVALID_ADDRESS");
        return (inline.ToString(), inline, null);
    }

    /// <summary>Сценарий 1 и 2 из ТЗ: создаёт заказ и доставку, затем пытается назначить свободного курьера.</summary>
    public (Delivery? d, bool courierAssigned, string? error) CreateOrder(CreateOrderRequest r)
    {
        if (r is null) return (null, false, "INVALID_REQUEST");
        Guid id;
        lock (_lock)
        {
            using var c = Open();
            if (Count(c, "SELECT COUNT(*) FROM customers WHERE id=$i", ("$i", r.CustomerId.ToString())) == 0) return (null, false, "CUSTOMER_NOT_FOUND");
            var (lines, e1) = ResolveItems(c, r.Items);
            if (lines is null) return (null, false, e1);
            var (text, inline, e2) = ResolveAddress(c, r.CustomerId, r.AddressId, r.DeliveryAddress);
            if (text is null) return (null, false, e2);

            var orderId = Guid.NewGuid(); id = Guid.NewGuid();
            using var tx = c.BeginTransaction();
            Cmd(c, "INSERT INTO orders(id,customer_id,address,comment,status,created_at) VALUES($i,$c,$a,$m,$s,$t)",
                ("$i", orderId.ToString()), ("$c", r.CustomerId.ToString()), ("$a", text),
                ("$m", string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim()), ("$s", nameof(OrderStatus.Created)), ("$t", Now())).ExecuteNonQuery();
            InsertItems(c, orderId, lines);
            OrderHist(c, orderId, OrderStatus.Created, "Заказ создан");
            InsertDelivery(c, id, orderId);
            if (r.SaveAddress && inline is not null)
                AddAddr(c, r.CustomerId, "", inline.City, inline.Street, inline.House, inline.Apartment);
            tx.Commit();
            _log.LogInformation("Создан заказ {Order}, доставка {Delivery}", orderId, id);
        }
        var assigned = TryAutoAssign(id);
        return (Delivery(id), assigned, null);
    }

    private static void InsertItems(SqliteConnection c, Guid orderId, List<(Product p, int q)> lines)
    {
        foreach (var (p, q) in lines)
            Cmd(c, "INSERT INTO order_items(order_id,product_id,name,price,quantity) VALUES($o,$p,$n,$pr,$q)",
                ("$o", orderId.ToString()), ("$p", p.Id.ToString()), ("$n", p.Name), ("$pr", (double)p.Price), ("$q", q)).ExecuteNonQuery();
    }

    private static void InsertDelivery(SqliteConnection c, Guid id, Guid orderId)
    {
        Cmd(c, "INSERT INTO deliveries(id,order_id,status,created_at) VALUES($i,$o,$s,$t)",
            ("$i", id.ToString()), ("$o", orderId.ToString()), ("$s", nameof(DeliveryStatus.Created)), ("$t", Now())).ExecuteNonQuery();
        Hist(c, id, DeliveryStatus.Created, "Доставка создана");
    }

    /// <summary>POST /api/deliveries: создание доставки для уже существующего заказа (идемпотентно по orderId).</summary>
    public (Delivery? d, string? error) CreateDelivery(Guid orderId)
    {
        lock (_lock)
        {
            using var c = Open();
            var existing = Deliveries(c, orderId: orderId).FirstOrDefault();
            if (existing is not null) return (existing, "DELIVERY_ALREADY_EXISTS");
            if (Count(c, "SELECT COUNT(*) FROM orders WHERE id=$i", ("$i", orderId.ToString())) == 0) return (null, "ORDER_NOT_FOUND");
            var id = Guid.NewGuid();
            using var tx = c.BeginTransaction();
            InsertDelivery(c, id, orderId);
            tx.Commit();
            return (Deliveries(c, deliveryId: id).First(), null);
        }
    }

    public string? UpdateOrder(Guid orderId, UpdateOrderRequest r)
    {
        lock (_lock)
        {
            using var c = Open();
            var d = Deliveries(c, orderId: orderId).FirstOrDefault();
            if (d is null) return "ORDER_NOT_FOUND";
            if (!OrderRules.CanEdit(d.Status)) return "ORDER_NOT_EDITABLE";
            List<(Product p, int q)>? lines = null;
            if (r.Items is not null) { var (l, e) = ResolveItems(c, r.Items); if (l is null) return e; lines = l; }
            string? addr = null;
            if (r.AddressId is not null || r.DeliveryAddress is not null)
            { var (t, _, e) = ResolveAddress(c, d.CustomerId, r.AddressId, r.DeliveryAddress); if (t is null) return e; addr = t; }
            using var tx = c.BeginTransaction();
            if (lines is not null)
            {
                Cmd(c, "DELETE FROM order_items WHERE order_id=$o", ("$o", orderId.ToString())).ExecuteNonQuery();
                InsertItems(c, orderId, lines);
            }
            Cmd(c, "UPDATE orders SET address=COALESCE($a,address), comment=$m WHERE id=$o",
                ("$a", addr), ("$m", string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim()), ("$o", orderId.ToString())).ExecuteNonQuery();
            OrderHist(c, orderId, d.OrderStatus, "Заказ изменён");
            tx.Commit();
            return null;
        }
    }

    // ---------- назначение курьера ----------
    private bool TryAutoAssign(Guid deliveryId) => Assign(deliveryId, null) is null;

    /// <summary>Назначает курьера. Без courierId выбирается свободный курьер автоматически (повторное назначение).</summary>
    public string? Assign(Guid id, Guid? courierId)
    {
        lock (_lock)
        {
            using var c = Open();
            var d = Deliveries(c, deliveryId: id).FirstOrDefault();
            if (d is null) return "DELIVERY_NOT_FOUND";
            if (d.Status != DeliveryStatus.Created)
                return DeliveryRules.IsFinal(d.Status) ? "DELIVERY_ALREADY_COMPLETED" : "DELIVERY_ALREADY_ASSIGNED";
            var all = Couriers(c);
            Courier? cr;
            if (courierId is Guid cid)
            {
                cr = all.FirstOrDefault(x => x.Id == cid);
                if (cr is null) return "COURIER_NOT_FOUND";
                if (!CourierRules.CanAssign(cr.State)) return "COURIER_NOT_AVAILABLE";
            }
            else
            {
                cr = all.Where(x => CourierRules.CanAssign(x.State)).FirstOrDefault();
                if (cr is null) return "COURIER_NOT_AVAILABLE";
            }
            using var tx = c.BeginTransaction();
            Cmd(c, "UPDATE couriers SET state='Busy' WHERE id=$i", ("$i", cr.Id.ToString())).ExecuteNonQuery();
            Cmd(c, "UPDATE deliveries SET courier_id=$c,status=$s WHERE id=$i", ("$c", cr.Id.ToString()), ("$s", nameof(DeliveryStatus.Assigned)), ("$i", id.ToString())).ExecuteNonQuery();
            Hist(c, id, DeliveryStatus.Assigned, "Назначен курьер " + cr.Name);
            SetOrderStatus(c, d.OrderId, d.OrderStatus, DeliveryRules.ToOrderStatus(DeliveryStatus.Assigned), null);
            tx.Commit();
            _log.LogInformation("Доставка {Delivery}: назначен курьер {Courier}", id, cr.Id);
            return null;
        }
    }

    private static void SetOrderStatus(SqliteConnection c, Guid orderId, OrderStatus from, OrderStatus to, string? comment)
    {
        if (from == to) return;
        Cmd(c, "UPDATE orders SET status=$s WHERE id=$o", ("$s", to.ToString()), ("$o", orderId.ToString())).ExecuteNonQuery();
        OrderHist(c, orderId, to, comment);
    }

    // ---------- смена статуса ----------
    private static readonly DeliveryStatus[] CourierTargets = { DeliveryStatus.Accepted, DeliveryStatus.InTransit, DeliveryStatus.Delivered, DeliveryStatus.Failed };

    public string? ChangeStatus(Guid id, DeliveryStatus s, string? comment, Session? actor = null)
    {
        lock (_lock)
        {
            using var c = Open();
            var d = Deliveries(c, deliveryId: id).FirstOrDefault();
            if (d is null) return "DELIVERY_NOT_FOUND";
            // права по ролям (null = внутренний вызов, например из тестов)
            if (actor is not null)
            {
                if (actor.Role == "client" && (actor.RefId != d.CustomerId || s != DeliveryStatus.Cancelled)) return "UNAUTHORIZED_STATUS_CHANGE";
                if (actor.Role == "courier" && (actor.RefId != d.CourierId || !CourierTargets.Contains(s))) return "UNAUTHORIZED_STATUS_CHANGE";
            }
            if (d.Status == s) return null; // повторное применение того же перехода — без изменений
            if (DeliveryRules.IsFinal(d.Status)) return "DELIVERY_ALREADY_COMPLETED";
            if (actor?.Role == "client" && !OrderRules.CanClientCancel(d.Status)) return "UNAUTHORIZED_STATUS_CHANGE";
            if (!DeliveryRules.CanTransition(d.Status, s)) return "INVALID_STATUS_TRANSITION";
            if (s == DeliveryStatus.Failed && string.IsNullOrWhiteSpace(comment)) return "FAILURE_REASON_REQUIRED";

            using var tx = c.BeginTransaction();
            var final = DeliveryRules.IsFinal(s);
            Cmd(c, @"UPDATE deliveries SET status=$s,
                       started_at=CASE WHEN $s='InTransit' AND started_at IS NULL THEN $t ELSE started_at END,
                       finished_at=CASE WHEN $f=1 THEN $t ELSE finished_at END,
                       failure_reason=CASE WHEN $s='Failed' THEN $m ELSE failure_reason END
                     WHERE id=$i", ("$s", s.ToString()), ("$t", Now()), ("$f", final ? 1 : 0), ("$m", comment?.Trim()), ("$i", id.ToString())).ExecuteNonQuery();
            Hist(c, id, s, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim());
            SetOrderStatus(c, d.OrderId, d.OrderStatus, DeliveryRules.ToOrderStatus(s), s == DeliveryStatus.Failed ? "Доставка не выполнена: " + comment!.Trim() : null);
            if (final && d.CourierId is Guid cid)
                Cmd(c, "UPDATE couriers SET state='Available' WHERE id=$i AND state='Busy'", ("$i", cid.ToString())).ExecuteNonQuery();
            tx.Commit();
            _log.LogInformation("Доставка {Delivery}: {From} → {To}", id, d.Status, s);
            return null;
        }
    }

    /// <summary>Клиент подтверждает получение заказа после статуса Delivered.</summary>
    public string? ConfirmReceipt(Guid id, Session? actor)
    {
        lock (_lock)
        {
            using var c = Open();
            var d = Deliveries(c, deliveryId: id).FirstOrDefault();
            if (d is null) return "DELIVERY_NOT_FOUND";
            if (actor is not null && actor.Role == "client" && actor.RefId != d.CustomerId) return "FORBIDDEN";
            if (d.Status != DeliveryStatus.Delivered) return "INVALID_STATUS_TRANSITION";
            if (d.ConfirmedAt is not null) return null;
            Cmd(c, "UPDATE deliveries SET confirmed_at=$t WHERE id=$i", ("$t", Now()), ("$i", id.ToString())).ExecuteNonQuery();
            return null;
        }
    }

    // ---------- состояние для интерфейса ----------
    public object Snapshot(Session? me = null)
    {
        using var c = Open();
        var deliveries = Deliveries(c);
        var customers = Customers(c);
        var couriers = Couriers(c);
        var addresses = Addresses(c);
        if (me?.Role == "client")
        {
            deliveries = deliveries.Where(d => d.CustomerId == me.RefId).ToList();
            customers = customers.Where(x => x.Id == me.RefId).ToList();
            addresses = addresses.Where(a => a.CustomerId == me.RefId).ToList();
            var ids = deliveries.Select(d => d.CourierId).ToHashSet();
            couriers = couriers.Where(x => ids.Contains(x.Id)).ToList();
        }
        else if (me?.Role == "courier")
        {
            deliveries = deliveries.Where(d => d.CourierId == me.RefId).ToList();
            var cu = deliveries.Select(d => d.CustomerId).ToHashSet();
            customers = customers.Where(x => cu.Contains(x.Id)).ToList();
            couriers = couriers.Where(x => x.Id == me.RefId).ToList();
            addresses = new();
        }
        return new
        {
            customers,
            couriers,
            addresses,
            products = Products(c),
            deliveries,
            stats = deliveries.GroupBy(x => x.Status.ToString()).ToDictionary(g => g.Key, g => g.Count())
        };
    }
}
