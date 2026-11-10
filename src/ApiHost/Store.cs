using System.Data.Common;
using System.Security.Cryptography;
using ApiHost.Db;
using CourierModule;
using CustomerModule;
using DeliveryModule;
using Npgsql;
using OrderModule;

namespace ApiHost;

/// <summary>
/// Хранилище на PostgreSQL и сценарии работы системы. Бизнес-правила берутся из модулей
/// (DeliveryRules, OrderRules, CourierRules, CustomerRules, Money), здесь они применяются к данным.
/// Согласованность обеспечивают транзакции и построчные блокировки (SELECT ... FOR UPDATE), а не блокировка процесса.
/// </summary>
public class Store : IDisposable
{
    private const string PgUniqueViolation = "23505";
    private const string PgForeignKeyViolation = "23503";

    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<Store> _log;

    public Store(IConfiguration cfg, ILogger<Store> log)
    {
        _log = log;
        var cs = cfg.GetConnectionString("Default")
                 ?? "Host=localhost;Port=5432;Database=delivery;Username=delivery;Password=delivery";
        _ds = NpgsqlDataSource.Create(cs);
        Migrator.Apply(_ds, Migrations.All, log: log);
        SeedIfEmpty();
    }

    public void Dispose() => _ds.Dispose();

    // ---------- инфраструктура ----------
    private NpgsqlConnection Open() => _ds.OpenConnection();

    private static NpgsqlCommand Cmd(NpgsqlConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private static long Count(NpgsqlConnection c, string sql, params (string, object?)[] ps) => (long)Cmd(c, sql, ps).ExecuteScalar()!;
    private static DateTime Now() => DateTime.UtcNow;
    private static DateTime? DtN(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? GuidN(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static bool IsPg(Exception e, string state) => e is PostgresException p && p.SqlState == state;

    private void SeedIfEmpty()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Cmd(c, "SELECT pg_advisory_xact_lock(7272002)").ExecuteNonQuery(); // два экземпляра API не засеют базу дважды
        if (Count(c, "SELECT COUNT(*) FROM users") == 0) { Seed(c); tx.Commit(); }
    }

    private void Seed(NpgsqlConnection c)
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
        _log.LogInformation("База данных пуста: добавлены тестовые данные");
    }

    // ---------- пользователи и вход ----------
    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

    private static void AddUser(NpgsqlConnection c, string login, string password, string role, Guid? refId)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        Cmd(c, "INSERT INTO users(id,login,salt,hash,role,ref_id) VALUES(@i,@l,@s,@h,@r,@f)",
            ("i", Guid.NewGuid()), ("l", login), ("s", salt), ("h", Hash(password, salt)), ("r", role), ("f", refId)).ExecuteNonQuery();
    }

    public Session? Authenticate(string? login, string? password)
    {
        if (string.IsNullOrWhiteSpace(login) || password is null) return null;
        using var c = Open();
        byte[] salt, hash; string role; Guid? refId;
        using (var r = Cmd(c, "SELECT salt,hash,role,ref_id FROM users WHERE lower(login)=lower(@l)", ("l", login.Trim())).ExecuteReader())
        {
            if (!r.Read()) return null;
            salt = (byte[])r["salt"]; hash = (byte[])r["hash"]; role = r.GetString(2); refId = GuidN(r, 3);
        }
        if (!CryptographicOperations.FixedTimeEquals(hash, Hash(password, salt))) return null;
        var name = role switch
        {
            "client" => Cmd(c, "SELECT name FROM customers WHERE id=@i", ("i", refId)).ExecuteScalar() as string,
            "courier" => Cmd(c, "SELECT name FROM couriers WHERE id=@i", ("i", refId)).ExecuteScalar() as string,
            _ => "Оператор"
        };
        return new Session(role, refId, name ?? login);
    }

    private static (string login, string password) NewCredentials(string? login, string? password, string prefix)
    {
        login = string.IsNullOrWhiteSpace(login) ? $"{prefix}{RandomNumberGenerator.GetInt32(1000, 9999)}" : login.Trim();
        password = string.IsNullOrWhiteSpace(password) ? RandomNumberGenerator.GetInt32(100000, 999999).ToString() : password;
        return (login, password);
    }

    // ---------- клиенты ----------
    private static Guid AddCustomer(NpgsqlConnection c, string name, string? phone)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO customers(id,name,phone) VALUES(@i,@n,@p)", ("i", id), ("n", name), ("p", phone ?? "")).ExecuteNonQuery();
        return id;
    }

    public (object? created, string? error) NewCustomer(NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return (null, e);
        using var c = Open();
        var (login, pwd) = NewCredentials(p.Login, p.Password, "client");
        try
        {
            using var tx = c.BeginTransaction();
            var id = AddCustomer(c, p.Name.Trim(), p.Phone);
            AddUser(c, login, pwd, "client", id);
            tx.Commit();
            return (new { id, name = p.Name.Trim(), phone = p.Phone ?? "", login, password = pwd }, null);
        }
        catch (Exception ex) when (IsPg(ex, PgUniqueViolation)) { return (null, "LOGIN_TAKEN"); }
    }

    public string? UpdateCustomer(Guid id, NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return e;
        using var c = Open();
        return Cmd(c, "UPDATE customers SET name=@n,phone=@p WHERE id=@i", ("n", p.Name.Trim()), ("p", p.Phone ?? ""), ("i", id)).ExecuteNonQuery() == 0 ? "CUSTOMER_NOT_FOUND" : null;
    }

    public string? DeleteCustomer(Guid id)
    {
        using var c = Open();
        try
        {
            using var tx = c.BeginTransaction();
            if (Count(c, "SELECT COUNT(*) FROM orders WHERE customer_id=@i", ("i", id)) > 0) return "CUSTOMER_HAS_ORDERS";
            Cmd(c, "DELETE FROM users WHERE ref_id=@i AND role='client'", ("i", id)).ExecuteNonQuery();
            var n = Cmd(c, "DELETE FROM customers WHERE id=@i", ("i", id)).ExecuteNonQuery();
            if (n == 0) return "CUSTOMER_NOT_FOUND";
            tx.Commit();
            return null;
        }
        catch (Exception ex) when (IsPg(ex, PgForeignKeyViolation)) { return "CUSTOMER_HAS_ORDERS"; }
    }

    public List<Customer> Customers() { using var c = Open(); return Customers(c); }
    private static List<Customer> Customers(NpgsqlConnection c)
    {
        using var r = Cmd(c, "SELECT id,name,phone FROM customers ORDER BY name").ExecuteReader();
        var l = new List<Customer>();
        while (r.Read()) l.Add(new Customer(r.GetGuid(0), r.GetString(1), r.GetString(2)));
        return l;
    }
    public Customer? Customer(Guid id) => Customers().FirstOrDefault(x => x.Id == id);

    // ---------- адреса клиента ----------
    private static Guid AddAddr(NpgsqlConnection c, Guid customerId, string label, string city, string street, string house, string? apt)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO customer_addresses(id,customer_id,label,city,street,house,apartment) VALUES(@i,@c,@l,@ci,@s,@h,@a)",
            ("i", id), ("c", customerId), ("l", label), ("ci", city.Trim()), ("s", street.Trim()), ("h", house.Trim()),
            ("a", string.IsNullOrWhiteSpace(apt) ? null : apt.Trim())).ExecuteNonQuery();
        return id;
    }

    private static List<SavedAddress> Addresses(NpgsqlConnection c, Guid? customerId = null)
    {
        using var r = Cmd(c, "SELECT id,customer_id,label,city,street,house,apartment FROM customer_addresses"
            + (customerId is null ? "" : " WHERE customer_id=@c") + " ORDER BY label,street",
            customerId is null ? Array.Empty<(string, object?)>() : new (string, object?)[] { ("c", customerId) }).ExecuteReader();
        var l = new List<SavedAddress>();
        while (r.Read()) l.Add(new SavedAddress(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Str(r, 6)));
        return l;
    }

    public List<SavedAddress> Addresses(Guid customerId) { using var c = Open(); return Addresses(c, customerId); }
    public SavedAddress? Address(Guid id) { using var c = Open(); return Addresses(c).FirstOrDefault(a => a.Id == id); }

    public (SavedAddress? a, string? error) AddAddress(Guid customerId, AddressRequest r)
    {
        if (CustomerRules.ValidateAddress(r.City, r.Street, r.House) is { } e) return (null, e);
        using var c = Open();
        if (Count(c, "SELECT COUNT(*) FROM customers WHERE id=@i", ("i", customerId)) == 0) return (null, "CUSTOMER_NOT_FOUND");
        var id = AddAddr(c, customerId, r.Label?.Trim() ?? "", r.City, r.Street, r.House, r.Apartment);
        return (Addresses(c, customerId).First(a => a.Id == id), null);
    }

    public string? UpdateAddress(Guid id, AddressRequest r)
    {
        if (CustomerRules.ValidateAddress(r.City, r.Street, r.House) is { } e) return e;
        using var c = Open();
        return Cmd(c, "UPDATE customer_addresses SET label=@l,city=@c,street=@s,house=@h,apartment=@a WHERE id=@i",
            ("l", r.Label?.Trim() ?? ""), ("c", r.City.Trim()), ("s", r.Street.Trim()), ("h", r.House.Trim()),
            ("a", string.IsNullOrWhiteSpace(r.Apartment) ? null : r.Apartment.Trim()), ("i", id)).ExecuteNonQuery() == 0 ? "ADDRESS_NOT_FOUND" : null;
    }

    public string? DeleteAddress(Guid id)
    {
        using var c = Open();
        return Cmd(c, "DELETE FROM customer_addresses WHERE id=@i", ("i", id)).ExecuteNonQuery() == 0 ? "ADDRESS_NOT_FOUND" : null;
    }

    // ---------- курьеры ----------
    private static Guid AddCourier(NpgsqlConnection c, string name, string? phone)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO couriers(id,name,phone,state) VALUES(@i,@n,@p,'Available')", ("i", id), ("n", name), ("p", phone ?? "")).ExecuteNonQuery();
        return id;
    }

    public (object? created, string? error) NewCourier(NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return (null, e);
        using var c = Open();
        var (login, pwd) = NewCredentials(p.Login, p.Password, "courier");
        try
        {
            using var tx = c.BeginTransaction();
            var id = AddCourier(c, p.Name.Trim(), p.Phone);
            AddUser(c, login, pwd, "courier", id);
            tx.Commit();
            return (new { id, name = p.Name.Trim(), phone = p.Phone ?? "", state = CourierState.Available, available = true, login, password = pwd }, null);
        }
        catch (Exception ex) when (IsPg(ex, PgUniqueViolation)) { return (null, "LOGIN_TAKEN"); }
    }

    public string? UpdateCourier(Guid id, NewPerson p)
    {
        if (CustomerRules.ValidateName(p.Name) is { } e) return e;
        using var c = Open();
        return Cmd(c, "UPDATE couriers SET name=@n,phone=@p WHERE id=@i", ("n", p.Name.Trim()), ("p", p.Phone ?? ""), ("i", id)).ExecuteNonQuery() == 0 ? "COURIER_NOT_FOUND" : null;
    }

    public string? DeleteCourier(Guid id)
    {
        using var c = Open();
        try
        {
            using var tx = c.BeginTransaction();
            if (Count(c, "SELECT COUNT(*) FROM deliveries WHERE courier_id=@i", ("i", id)) > 0) return "COURIER_HAS_ORDERS";
            Cmd(c, "DELETE FROM users WHERE ref_id=@i AND role='courier'", ("i", id)).ExecuteNonQuery();
            var n = Cmd(c, "DELETE FROM couriers WHERE id=@i", ("i", id)).ExecuteNonQuery();
            if (n == 0) return "COURIER_NOT_FOUND";
            tx.Commit();
            return null;
        }
        catch (Exception ex) when (IsPg(ex, PgForeignKeyViolation)) { return "COURIER_HAS_ORDERS"; }
    }

    public List<Courier> Couriers(bool onlyAvailable = false) { using var c = Open(); return Couriers(c, onlyAvailable); }
    private static List<Courier> Couriers(NpgsqlConnection c, bool onlyAvailable = false)
    {
        using var r = Cmd(c, "SELECT id,name,phone,state FROM couriers" + (onlyAvailable ? " WHERE state='Available'" : "") + " ORDER BY name").ExecuteReader();
        var l = new List<Courier>();
        while (r.Read()) l.Add(new Courier(r.GetGuid(0), r.GetString(1), r.GetString(2), Enum.Parse<CourierState>(r.GetString(3))));
        return l;
    }

    public string? SetCourierState(Guid id, CourierState state, bool byOperator)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var cur = Cmd(c, "SELECT state FROM couriers WHERE id=@i FOR UPDATE", ("i", id)).ExecuteScalar() as string;
        if (cur is null) return "COURIER_NOT_FOUND";
        var from = Enum.Parse<CourierState>(cur);
        if (CourierRules.ValidateStateChange(from, state, byOperator) is { } e) return e;
        Cmd(c, "UPDATE couriers SET state=@s WHERE id=@i", ("s", state.ToString()), ("i", id)).ExecuteNonQuery();
        tx.Commit();
        _log.LogInformation("Курьер {Courier}: состояние {From} → {To}", id, from, state);
        return null;
    }

    // ---------- товары (цены хранятся в копейках) ----------
    private static Guid AddProduct(NpgsqlConnection c, string name, decimal price)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO products(id,name,price_kopecks) VALUES(@i,@n,@p)", ("i", id), ("n", name), ("p", Money.ToKopecks(price))).ExecuteNonQuery();
        return id;
    }

    public List<Product> Products() { using var c = Open(); return Products(c); }
    private static List<Product> Products(NpgsqlConnection c)
    {
        using var r = Cmd(c, "SELECT id,name,price_kopecks FROM products ORDER BY name").ExecuteReader();
        var l = new List<Product>();
        while (r.Read()) l.Add(new Product(r.GetGuid(0), r.GetString(1), Money.FromKopecks(r.GetInt64(2))));
        return l;
    }

    private static bool ValidProduct(ProductRequest r) => !string.IsNullOrWhiteSpace(r.Name) && Money.IsValidPrice(r.Price);

    public (Product? p, string? error) NewProduct(ProductRequest r)
    {
        if (!ValidProduct(r)) return (null, "INVALID_PRODUCT");
        using var c = Open();
        var id = AddProduct(c, r.Name.Trim(), r.Price);
        return (new Product(id, r.Name.Trim(), Money.FromKopecks(Money.ToKopecks(r.Price))), null);
    }

    public string? UpdateProduct(Guid id, ProductRequest r)
    {
        if (!ValidProduct(r)) return "INVALID_PRODUCT";
        using var c = Open();
        return Cmd(c, "UPDATE products SET name=@n,price_kopecks=@p WHERE id=@i", ("n", r.Name.Trim()), ("p", Money.ToKopecks(r.Price)), ("i", id)).ExecuteNonQuery() == 0 ? "PRODUCT_NOT_FOUND" : null;
    }

    public string? DeleteProduct(Guid id)
    {
        using var c = Open();
        return Cmd(c, "DELETE FROM products WHERE id=@i", ("i", id)).ExecuteNonQuery() == 0 ? "PRODUCT_NOT_FOUND" : null;
    }

    // ---------- чтение заказов и доставок ----------
    public List<Delivery> Deliveries(string? q = null, DeliveryStatus? status = null, Guid? courierId = null, Guid? customerId = null)
    {
        using var c = Open();
        return Deliveries(c, q, status, courierId, customerId);
    }

    private static List<Delivery> Deliveries(NpgsqlConnection c, string? q = null, DeliveryStatus? status = null, Guid? courierId = null, Guid? customerId = null, Guid? deliveryId = null, Guid? orderId = null)
    {
        var sql = @"SELECT d.id,d.order_id,o.customer_id,d.courier_id,o.address,o.comment,d.status,o.status,o.created_at,
                           d.started_at,d.finished_at,d.failure_reason,d.confirmed_at
                    FROM deliveries d JOIN orders o ON o.id=d.order_id WHERE 1=1";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            sql += @" AND (o.address ILIKE @q OR o.comment ILIKE @q OR d.id::text ILIKE @q OR o.id::text ILIKE @q
                       OR EXISTS(SELECT 1 FROM order_items i WHERE i.order_id=o.id AND i.name ILIKE @q))";
            ps.Add(("q", "%" + q.Trim() + "%"));
        }
        if (status is not null) { sql += " AND d.status=@s"; ps.Add(("s", status.ToString())); }
        if (courierId is not null) { sql += " AND d.courier_id=@c"; ps.Add(("c", courierId)); }
        if (customerId is not null) { sql += " AND o.customer_id=@u"; ps.Add(("u", customerId)); }
        if (deliveryId is not null) { sql += " AND d.id=@d"; ps.Add(("d", deliveryId)); }
        if (orderId is not null) { sql += " AND d.order_id=@o"; ps.Add(("o", orderId)); }
        sql += " ORDER BY o.created_at DESC";
        var list = new List<Delivery>();
        using (var r = Cmd(c, sql, ps.ToArray()).ExecuteReader())
            while (r.Read())
                list.Add(new Delivery
                {
                    DeliveryId = r.GetGuid(0),
                    OrderId = r.GetGuid(1),
                    CustomerId = r.GetGuid(2),
                    CourierId = GuidN(r, 3),
                    Address = r.GetString(4),
                    Comment = Str(r, 5),
                    Status = Enum.Parse<DeliveryStatus>(r.GetString(6)),
                    OrderStatus = Enum.Parse<OrderStatus>(r.GetString(7)),
                    CreatedAt = r.GetDateTime(8),
                    StartedAt = DtN(r, 9),
                    FinishedAt = DtN(r, 10),
                    FailureReason = Str(r, 11),
                    ConfirmedAt = DtN(r, 12)
                });
        if (list.Count == 0) return list;

        // дочерние данные одним запросом на таблицу, а не по запросу на каждую доставку
        var byOrder = list.ToDictionary(d => d.OrderId);
        var byDelivery = list.ToDictionary(d => d.DeliveryId);
        var orderIds = byOrder.Keys.ToArray();
        var deliveryIds = byDelivery.Keys.ToArray();
        var kopecks = list.ToDictionary(d => d.OrderId, _ => new List<(long, int)>());
        using (var i = Cmd(c, "SELECT order_id,product_id,name,price_kopecks,quantity FROM order_items WHERE order_id = ANY(@o) ORDER BY id", ("o", orderIds)).ExecuteReader())
            while (i.Read())
            {
                var oid = i.GetGuid(0);
                byOrder[oid].Items.Add(new OrderItemDto(i.GetGuid(1), i.GetString(2), Money.FromKopecks(i.GetInt64(3)), i.GetInt32(4)));
                kopecks[oid].Add((i.GetInt64(3), i.GetInt32(4)));
            }
        using (var h = Cmd(c, "SELECT delivery_id,status,at,comment FROM status_history WHERE delivery_id = ANY(@d) ORDER BY id", ("d", deliveryIds)).ExecuteReader())
            while (h.Read()) byDelivery[h.GetGuid(0)].History.Add(new StatusChange(Enum.Parse<DeliveryStatus>(h.GetString(1)), h.GetDateTime(2), Str(h, 3)));
        using (var h = Cmd(c, "SELECT order_id,status,at,comment FROM order_history WHERE order_id = ANY(@o) ORDER BY id", ("o", orderIds)).ExecuteReader())
            while (h.Read()) byOrder[h.GetGuid(0)].OrderHistory.Add(new OrderStatusChange(Enum.Parse<OrderStatus>(h.GetString(1)), h.GetDateTime(2), Str(h, 3)));
        foreach (var d in list)
        {
            d.Description = string.Join(", ", d.Items.Select(x => $"{x.Name} ×{x.Quantity}"));
            d.Total = Money.FromKopecks(Money.Total(kopecks[d.OrderId]));
        }
        return list;
    }

    public Delivery? Delivery(Guid id) { using var c = Open(); return Deliveries(c, deliveryId: id).FirstOrDefault(); }
    public Delivery? Order(Guid id) { using var c = Open(); return Deliveries(c, orderId: id).FirstOrDefault(); }

    private static void Hist(NpgsqlConnection c, Guid deliveryId, DeliveryStatus s, string? comment) =>
        Cmd(c, "INSERT INTO status_history(delivery_id,status,at,comment) VALUES(@d,@s,@a,@m)",
            ("d", deliveryId), ("s", s.ToString()), ("a", Now()), ("m", comment)).ExecuteNonQuery();

    private static void OrderHist(NpgsqlConnection c, Guid orderId, OrderStatus s, string? comment) =>
        Cmd(c, "INSERT INTO order_history(order_id,status,at,comment) VALUES(@o,@s,@a,@m)",
            ("o", orderId), ("s", s.ToString()), ("a", Now()), ("m", comment)).ExecuteNonQuery();

    // ---------- создание и изменение заказа ----------
    private static (List<(Product p, int q)>? lines, string? error) ResolveItems(NpgsqlConnection c, List<ItemRequest>? items)
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

    private static (string? text, AddressDto? inline, string? error) ResolveAddress(NpgsqlConnection c, Guid customerId, Guid? addressId, AddressDto? inline)
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
        using (var c = Open())
        {
            if (Count(c, "SELECT COUNT(*) FROM customers WHERE id=@i", ("i", r.CustomerId)) == 0) return (null, false, "CUSTOMER_NOT_FOUND");
            if (ExistingByRequest(c, r) is { } dup) return (dup, dup.CourierId is not null, null); // повторный запрос
            var (lines, e1) = ResolveItems(c, r.Items);
            if (lines is null) return (null, false, e1);
            var (text, inline, e2) = ResolveAddress(c, r.CustomerId, r.AddressId, r.DeliveryAddress);
            if (text is null) return (null, false, e2);

            var orderId = Guid.NewGuid(); id = Guid.NewGuid();
            using var tx = c.BeginTransaction();
            try
            {
                Cmd(c, "INSERT INTO orders(id,customer_id,address,comment,status,created_at,request_id) VALUES(@i,@c,@a,@m,@s,@t,@rq)",
                    ("i", orderId), ("c", r.CustomerId), ("a", text),
                    ("m", string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim()), ("s", nameof(OrderStatus.Created)), ("t", Now()), ("rq", r.RequestId)).ExecuteNonQuery();
                InsertItems(c, orderId, lines);
                OrderHist(c, orderId, OrderStatus.Created, "Заказ создан");
                InsertDelivery(c, id, orderId);
                if (r.SaveAddress && inline is not null)
                    AddAddr(c, r.CustomerId, "", inline.City, inline.Street, inline.House, inline.Apartment);
                tx.Commit(); // заказ, позиции, история и доставка сохраняются атомарно
            }
            catch (Exception ex) when (IsPg(ex, PgUniqueViolation) && r.RequestId is not null)
            {
                // параллельный дубль того же запроса уже создал заказ
                tx.Rollback();
                using var c2 = Open();
                if (ExistingByRequest(c2, r) is { } dup2) return (dup2, dup2.CourierId is not null, null);
                throw;
            }
            _log.LogInformation("Создан заказ {Order}, доставка {Delivery}", orderId, id);
        }
        // Назначение курьера — отдельный шаг: его сбой не отменяет уже сохранённый заказ (сценарий 2 из ТЗ).
        var assigned = TryAutoAssign(id);
        return (Delivery(id), assigned, null);
    }

    private static Delivery? ExistingByRequest(NpgsqlConnection c, CreateOrderRequest r)
    {
        if (r.RequestId is not Guid rid) return null;
        return Cmd(c, "SELECT id FROM orders WHERE customer_id=@c AND request_id=@r", ("c", r.CustomerId), ("r", rid)).ExecuteScalar() is Guid oid
            ? Deliveries(c, orderId: oid).FirstOrDefault() : null;
    }

    private static void InsertItems(NpgsqlConnection c, Guid orderId, List<(Product p, int q)> lines)
    {
        foreach (var (p, q) in lines)
            Cmd(c, "INSERT INTO order_items(order_id,product_id,name,price_kopecks,quantity) VALUES(@o,@p,@n,@pr,@q)",
                ("o", orderId), ("p", p.Id), ("n", p.Name), ("pr", Money.ToKopecks(p.Price)), ("q", q)).ExecuteNonQuery();
    }

    private static void InsertDelivery(NpgsqlConnection c, Guid id, Guid orderId)
    {
        Cmd(c, "INSERT INTO deliveries(id,order_id,status,created_at) VALUES(@i,@o,@s,@t)",
            ("i", id), ("o", orderId), ("s", nameof(DeliveryStatus.Created)), ("t", Now())).ExecuteNonQuery();
        Hist(c, id, DeliveryStatus.Created, "Доставка создана");
    }

    /// <summary>POST /api/deliveries: создание доставки для уже существующего заказа (идемпотентно по orderId).</summary>
    public (Delivery? d, string? error) CreateDelivery(Guid orderId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        // блокировка заказа: два одновременных запроса не создадут две доставки
        if (Cmd(c, "SELECT 1 FROM orders WHERE id=@i FOR UPDATE", ("i", orderId)).ExecuteScalar() is null) return (null, "ORDER_NOT_FOUND");
        var existing = Deliveries(c, orderId: orderId).FirstOrDefault();
        if (existing is not null) return (existing, "DELIVERY_ALREADY_EXISTS");
        var id = Guid.NewGuid();
        InsertDelivery(c, id, orderId);
        tx.Commit();
        return (Deliveries(c, deliveryId: id).First(), null);
    }

    public string? UpdateOrder(Guid orderId, UpdateOrderRequest r)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Cmd(c, "SELECT 1 FROM orders WHERE id=@i FOR UPDATE", ("i", orderId)).ExecuteScalar() is null) return "ORDER_NOT_FOUND";
        var d = Deliveries(c, orderId: orderId).First();
        if (!OrderRules.CanEdit(d.Status)) return "ORDER_NOT_EDITABLE";
        List<(Product p, int q)>? lines = null;
        if (r.Items is not null) { var (l, e) = ResolveItems(c, r.Items); if (l is null) return e; lines = l; }
        string? addr = null;
        if (r.AddressId is not null || r.DeliveryAddress is not null)
        { var (t, _, e) = ResolveAddress(c, d.CustomerId, r.AddressId, r.DeliveryAddress); if (t is null) return e; addr = t; }
        if (lines is not null)
        {
            Cmd(c, "DELETE FROM order_items WHERE order_id=@o", ("o", orderId)).ExecuteNonQuery();
            InsertItems(c, orderId, lines);
        }
        // comment == null — не менять; пустая строка — очистить
        Cmd(c, "UPDATE orders SET address=COALESCE(@a::text,address), comment=CASE WHEN @hc THEN @m::text ELSE comment END WHERE id=@o",
            ("a", addr), ("hc", r.Comment is not null), ("m", string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment?.Trim()), ("o", orderId)).ExecuteNonQuery();
        OrderHist(c, orderId, d.OrderStatus, "Заказ изменён");
        tx.Commit();
        return null;
    }

    // ---------- назначение курьера ----------
    private bool TryAutoAssign(Guid deliveryId)
    {
        try { return Assign(deliveryId, null) is null; }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            // заказ уже сохранён, доставка осталась Created: оператор назначит курьера позже
            _log.LogError(ex, "Не удалось автоматически назначить курьера для доставки {Delivery}", deliveryId);
            return false;
        }
    }

    /// <summary>Назначает курьера. Без courierId выбирается свободный курьер автоматически (повторное назначение).</summary>
    public string? Assign(Guid id, Guid? courierId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Cmd(c, "SELECT 1 FROM deliveries WHERE id=@i FOR UPDATE", ("i", id)).ExecuteScalar() is null) return "DELIVERY_NOT_FOUND";
        var d = Deliveries(c, deliveryId: id).First();
        if (d.Status != DeliveryStatus.Created)
            return DeliveryRules.IsFinal(d.Status) ? "DELIVERY_ALREADY_COMPLETED" : "DELIVERY_ALREADY_ASSIGNED";
        (Guid Id, string Name, CourierState State)? cr;
        // FOR UPDATE: один курьер не достанется двум доставкам одновременно; SKIP LOCKED — берём следующего свободного
        var sel = "SELECT id,name,state FROM couriers WHERE " + (courierId is null ? "state='Available' ORDER BY name FOR UPDATE SKIP LOCKED LIMIT 1" : "id=@c FOR UPDATE");
        using (var r = Cmd(c, sel, courierId is null ? Array.Empty<(string, object?)>() : new (string, object?)[] { ("c", courierId) }).ExecuteReader())
            cr = r.Read() ? (r.GetGuid(0), r.GetString(1), Enum.Parse<CourierState>(r.GetString(2))) : null;
        if (cr is null) return courierId is null ? "COURIER_NOT_AVAILABLE" : "COURIER_NOT_FOUND";
        if (!CourierRules.CanAssign(cr.Value.State)) return "COURIER_NOT_AVAILABLE";
        Cmd(c, "UPDATE couriers SET state='Busy' WHERE id=@i", ("i", cr.Value.Id)).ExecuteNonQuery();
        Cmd(c, "UPDATE deliveries SET courier_id=@c,status=@s WHERE id=@i", ("c", cr.Value.Id), ("s", nameof(DeliveryStatus.Assigned)), ("i", id)).ExecuteNonQuery();
        Hist(c, id, DeliveryStatus.Assigned, "Назначен курьер " + cr.Value.Name);
        SetOrderStatus(c, d.OrderId, d.OrderStatus, DeliveryRules.ToOrderStatus(DeliveryStatus.Assigned), null);
        tx.Commit();
        _log.LogInformation("Доставка {Delivery}: назначен курьер {Courier}", id, cr.Value.Id);
        return null;
    }

    private static void SetOrderStatus(NpgsqlConnection c, Guid orderId, OrderStatus from, OrderStatus to, string? comment)
    {
        if (from == to) return;
        Cmd(c, "UPDATE orders SET status=@s WHERE id=@o", ("s", to.ToString()), ("o", orderId)).ExecuteNonQuery();
        OrderHist(c, orderId, to, comment);
    }

    // ---------- смена статуса ----------
    private static readonly DeliveryStatus[] CourierTargets = { DeliveryStatus.Accepted, DeliveryStatus.InTransit, DeliveryStatus.Delivered, DeliveryStatus.Failed };

    public string? ChangeStatus(Guid id, DeliveryStatus s, string? comment, Session? actor = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Cmd(c, "SELECT 1 FROM deliveries WHERE id=@i FOR UPDATE", ("i", id)).ExecuteScalar() is null) return "DELIVERY_NOT_FOUND";
        var d = Deliveries(c, deliveryId: id).First();
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

        var final = DeliveryRules.IsFinal(s);
        Cmd(c, @"UPDATE deliveries SET status=@s,
                   started_at=CASE WHEN @s='InTransit' AND started_at IS NULL THEN @t ELSE started_at END,
                   finished_at=CASE WHEN @f THEN @t ELSE finished_at END,
                   failure_reason=CASE WHEN @s='Failed' THEN @m::text ELSE failure_reason END
                 WHERE id=@i", ("s", s.ToString()), ("t", Now()), ("f", final), ("m", comment?.Trim()), ("i", id)).ExecuteNonQuery();
        Hist(c, id, s, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim());
        SetOrderStatus(c, d.OrderId, d.OrderStatus, DeliveryRules.ToOrderStatus(s), s == DeliveryStatus.Failed ? "Доставка не выполнена: " + comment!.Trim() : null);
        if (final && d.CourierId is Guid cid)
            Cmd(c, "UPDATE couriers SET state='Available' WHERE id=@i AND state='Busy'", ("i", cid)).ExecuteNonQuery();
        tx.Commit();
        _log.LogInformation("Доставка {Delivery}: {From} → {To}", id, d.Status, s);
        return null;
    }

    /// <summary>Клиент подтверждает получение заказа после статуса Delivered.</summary>
    public string? ConfirmReceipt(Guid id, Session? actor)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Cmd(c, "SELECT 1 FROM deliveries WHERE id=@i FOR UPDATE", ("i", id)).ExecuteScalar() is null) return "DELIVERY_NOT_FOUND";
        var d = Deliveries(c, deliveryId: id).First();
        if (actor is not null && actor.Role == "client" && actor.RefId != d.CustomerId) return "FORBIDDEN";
        if (d.Status != DeliveryStatus.Delivered) return "INVALID_STATUS_TRANSITION";
        if (d.ConfirmedAt is not null) return null;
        Cmd(c, "UPDATE deliveries SET confirmed_at=@t WHERE id=@i", ("t", Now()), ("i", id)).ExecuteNonQuery();
        tx.Commit();
        return null;
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
