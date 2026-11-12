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
    public const int MinPasswordLength = 8;

    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<Store> _log;

    /// <param name="env">Окружение хоста. Если Seed:Demo не задан, демо-данные создаются только в Development
    /// (без окружения — например, в модульных тестах — как раньше, с демо-данными).</param>
    public Store(IConfiguration cfg, ILogger<Store> log, IHostEnvironment? env = null)
    {
        _log = log;
        // без запасной строки со стандартным паролем: адрес и учётные данные БД задаются конфигурацией
        var cs = cfg.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Default (переменная окружения ConnectionStrings__Default)");
        _ds = NpgsqlDataSource.Create(cs);
        Migrator.Apply(_ds, Migrations.All, log: log);
        SeedIfEmpty(cfg.GetValue("Seed:Demo", env is null || StartupChecks.DemoByDefault(env)), cfg["Seed:OperatorPassword"]);
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

    /// <summary>
    /// Ошибка хранилища: любая ошибка PostgreSQL/Npgsql, таймаут или сетевая ошибка. Клиент получает STORAGE_ERROR
    /// без подробностей. Когда база остановлена, Npgsql может выбросить SocketException без обёртки в NpgsqlException.
    /// </summary>
    public static bool IsStorageError(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is DbException or TimeoutException or System.Net.Sockets.SocketException or IOException) return true;
        return false;
    }

    /// <summary>
    /// Временная недоступность хранилища, которая проходит сама: нет соединения, таймаут, перезапуск сервера БД,
    /// взаимоблокировка. Ошибки данных и SQL (нарушение ограничения, ошибка в триггере) временными не считаются:
    /// повтор того же запроса к ним не приведёт к успеху.
    /// </summary>
    public static bool IsTransient(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is NpgsqlException { IsTransient: true } or TimeoutException or System.Net.Sockets.SocketException or IOException) return true;
            if (e is PostgresException) return false;
        }
        return false;
    }

    private void SeedIfEmpty(bool demo, string? operatorPassword)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Cmd(c, "SELECT pg_advisory_xact_lock(7272002)").ExecuteNonQuery(); // два экземпляра API не засеют базу дважды
        if (Count(c, "SELECT COUNT(*) FROM users") != 0) return;
        if (demo) Seed(c);
        else
        {
            // боевой режим: без демо-данных и без предсказуемых паролей — оператор создаётся с заданным паролем
            if (operatorPassword is null || StartupChecks.IsWeak(operatorPassword, MinPasswordLength, "operator"))
                throw new InvalidOperationException($"Seed:Demo=false: задайте Seed:OperatorPassword (не короче {MinPasswordLength} символов, не равный логину) для первого входа оператора");
            AddUser(c, "operator", operatorPassword, "operator", null);
            _log.LogInformation("База данных пуста: создан оператор без демо-данных");
        }
        tx.Commit();
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

    // соль для «пустой» проверки пароля: ответ на несуществующий логин занимает столько же времени, сколько на существующий
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(16);

    public Session? Authenticate(string? login, string? password)
    {
        if (string.IsNullOrWhiteSpace(login) || password is null) return null;
        using var c = Open();
        byte[] salt, hash; string role; Guid? refId; Guid userId;
        using (var r = Cmd(c, "SELECT salt,hash,role,ref_id,id FROM users WHERE lower(login)=lower(@l)", ("l", login.Trim())).ExecuteReader())
        {
            if (!r.Read())
            {
                Hash(password, DummySalt); // не раскрываем по времени ответа, существует ли логин
                return null;
            }
            salt = (byte[])r["salt"]; hash = (byte[])r["hash"]; role = r.GetString(2); refId = GuidN(r, 3); userId = r.GetGuid(4);
        }
        if (!CryptographicOperations.FixedTimeEquals(hash, Hash(password, salt))) return null;
        var name = role switch
        {
            "client" => Cmd(c, "SELECT name FROM customers WHERE id=@i", ("i", refId)).ExecuteScalar() as string,
            "courier" => Cmd(c, "SELECT name FROM couriers WHERE id=@i", ("i", refId)).ExecuteScalar() as string,
            _ => "Оператор"
        };
        return new Session(role, refId, name ?? login, userId);
    }

    // ---------- сессии ----------
    public void SaveSession(byte[] tokenHash, Session s, DateTime expires)
    {
        using var c = Open();
        Cmd(c, "DELETE FROM sessions WHERE expires_at < now()").ExecuteNonQuery();
        Cmd(c, "INSERT INTO sessions(token_hash,user_id,role,ref_id,name,expires_at) VALUES(@h,@u,@r,@f,@n,@e)",
            ("h", tokenHash), ("u", s.UserId), ("r", s.Role), ("f", s.RefId), ("n", s.Name), ("e", expires)).ExecuteNonQuery();
    }

    public Session? LoadSession(byte[] tokenHash)
    {
        using var c = Open();
        // роль и связь берутся из users: сессия удалённой учётной записи недействительна, смена роли применяется сразу;
        // имя — текущее из карточки клиента или курьера, а не сохранённое при входе
        using var r = Cmd(c, @"SELECT u.role,u.ref_id,COALESCE(cu.name,co.name,s.name),s.user_id FROM sessions s JOIN users u ON u.id=s.user_id
                               LEFT JOIN customers cu ON u.role='client' AND cu.id=u.ref_id
                               LEFT JOIN couriers co ON u.role='courier' AND co.id=u.ref_id
                               WHERE s.token_hash=@h AND s.expires_at > now()", ("h", tokenHash)).ExecuteReader();
        return r.Read() ? new Session(r.GetString(0), GuidN(r, 1), r.GetString(2), GuidN(r, 3)) : null;
    }

    // ---------- ограничение частоты (неудачные входы, регистрация) ----------
    /// <summary>
    /// Увеличивает счётчик по ключу и возвращает его новое значение. Если окно истекло, отсчёт начинается заново.
    /// Счётчики хранятся в PostgreSQL: общие для всех экземпляров API и не сбрасываются перезапуском.
    /// </summary>
    public int RateLimitHit(string key, TimeSpan window, DateTimeOffset now)
    {
        using var c = Open();
        var hits = (int)Cmd(c, @"INSERT INTO rate_limits(key,hits,window_start) VALUES(@k,1,@n)
            ON CONFLICT(key) DO UPDATE SET
              hits = CASE WHEN rate_limits.window_start <= @n - @w THEN 1 ELSE rate_limits.hits + 1 END,
              window_start = CASE WHEN rate_limits.window_start <= @n - @w THEN @n ELSE rate_limits.window_start END
            RETURNING hits", ("k", key), ("n", now.UtcDateTime), ("w", window)).ExecuteScalar()!;
        if (RandomNumberGenerator.GetInt32(100) == 0) // изредка удаляем устаревшие записи, чтобы таблица не росла
            Cmd(c, "DELETE FROM rate_limits WHERE window_start < @t", ("t", (now - TimeSpan.FromDays(1)).UtcDateTime)).ExecuteNonQuery();
        return hits;
    }

    /// <summary>Текущее значение счётчика в пределах окна (0, если окно истекло или записи нет).</summary>
    public int RateLimitCount(string key, TimeSpan window, DateTimeOffset now)
    {
        using var c = Open();
        return Cmd(c, "SELECT hits FROM rate_limits WHERE key=@k AND window_start > @n - @w", ("k", key), ("n", now.UtcDateTime), ("w", window)).ExecuteScalar() is int n ? n : 0;
    }

    public void RateLimitReset(string key)
    {
        using var c = Open();
        Cmd(c, "DELETE FROM rate_limits WHERE key=@k", ("k", key)).ExecuteNonQuery();
    }

    public void DeleteSession(byte[] tokenHash)
    {
        using var c = Open();
        Cmd(c, "DELETE FROM sessions WHERE token_hash=@h", ("h", tokenHash)).ExecuteNonQuery();
    }

    /// <summary>Смена пароля: проверяет старый, сохраняет новый и завершает все остальные сессии пользователя.</summary>
    public string? ChangePassword(Session me, string? oldPassword, string? newPassword, byte[]? keepSession)
    {
        if (me.UserId is not Guid uid) return "FORBIDDEN";
        if (newPassword is null || newPassword.Length < MinPasswordLength || newPassword == oldPassword) return "WEAK_PASSWORD";
        using var c = Open();
        using var tx = c.BeginTransaction();
        byte[] salt, hash; string login;
        using (var r = Cmd(c, "SELECT salt,hash,login FROM users WHERE id=@i FOR UPDATE", ("i", uid)).ExecuteReader())
        {
            if (!r.Read()) return "FORBIDDEN";
            salt = (byte[])r["salt"]; hash = (byte[])r["hash"]; login = r.GetString(2);
        }
        if (oldPassword is null || !CryptographicOperations.FixedTimeEquals(hash, Hash(oldPassword, salt))) return "INVALID_CREDENTIALS";
        if (StartupChecks.IsWeak(newPassword, MinPasswordLength, login)) return "WEAK_PASSWORD"; // не равен логину и не из списка стандартных
        var ns = RandomNumberGenerator.GetBytes(16);
        Cmd(c, "UPDATE users SET salt=@s,hash=@h WHERE id=@i", ("s", ns), ("h", Hash(newPassword, ns)), ("i", uid)).ExecuteNonQuery();
        Cmd(c, "DELETE FROM sessions WHERE user_id=@u AND (@k::bytea IS NULL OR token_hash<>@k::bytea)", ("u", uid), ("k", keepSession)).ExecuteNonQuery();
        tx.Commit();
        return null;
    }

    private const string PasswordAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Случайный пароль из 12 символов без похожих букв и цифр (l/1, O/0).</summary>
    private static string GeneratePassword() => RandomNumberGenerator.GetString(PasswordAlphabet, 12);

    /// <summary>
    /// Учётные данные новой записи. Заданный оператором пароль проверяется так же, как при смене пароля;
    /// если не задан — генерируется стойкий (раньше — 6 цифр, короче минимальной длины пароля).
    /// </summary>
    private static (string login, string password, bool generatedLogin, string? error) NewCredentials(string? login, string? password, string prefix)
    {
        var generated = string.IsNullOrWhiteSpace(login);
        // логин, заданный оператором, проверяется по тем же правилам, что и при самостоятельной регистрации
        if (!generated && ValidateLogin(login) is { } le) return (login!, password ?? "", false, le);
        login = generated ? $"{prefix}{RandomNumberGenerator.GetInt32(1000, 10000)}" : login!.Trim();
        if (string.IsNullOrWhiteSpace(password)) password = GeneratePassword();
        else if (StartupChecks.IsWeak(password, MinPasswordLength, login)) return (login, password, generated, "WEAK_PASSWORD");
        return (login, password, generated, null);
    }

    /// <summary>Создаёт запись и учётную запись в одной транзакции; сгенерированный логин при совпадении подбирается заново.</summary>
    private (object? created, string? error) CreateWithUser(NewPerson p, string prefix, string role, Func<NpgsqlConnection, Guid> insert, Func<Guid, string, string, object> result)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (login, pwd, generated, e) = NewCredentials(p.Login, p.Password, prefix);
            if (e is not null) return (null, e);
            using var c = Open();
            try
            {
                using var tx = c.BeginTransaction();
                var id = insert(c);
                AddUser(c, login, pwd, role, id);
                tx.Commit();
                return (result(id, login, pwd), null);
            }
            catch (Exception ex) when (IsPg(ex, PgUniqueViolation))
            {
                if (!generated || attempt >= 4) return (null, "LOGIN_TAKEN");
            }
        }
    }

    /// <summary>Удаляет учётные записи вместе с их сессиями: токен удалённого пользователя перестаёт действовать сразу.</summary>
    private static void DeleteUsers(NpgsqlConnection c, Guid refId, string role)
    {
        Cmd(c, "DELETE FROM sessions WHERE user_id IN (SELECT id FROM users WHERE ref_id=@i AND role=@r)", ("i", refId), ("r", role)).ExecuteNonQuery();
        Cmd(c, "DELETE FROM users WHERE ref_id=@i AND role=@r", ("i", refId), ("r", role)).ExecuteNonQuery();
    }

    // ---------- клиенты ----------
    private static Guid AddCustomer(NpgsqlConnection c, string name, string? phone)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO customers(id,name,phone) VALUES(@i,@n,@p)", ("i", id), ("n", name), ("p", phone ?? "")).ExecuteNonQuery();
        return id;
    }

    /// <summary>Имя обязательно (до 100 символов), телефон необязателен (до 32 символов).</summary>
    private static string? ValidatePerson(NewPerson? p) =>
        p is null ? "INVALID_REQUEST" : CustomerRules.ValidateName(p.Name) ?? CustomerRules.ValidatePhone(p.Phone);

    private static string PhoneOf(NewPerson p) => p.Phone?.Trim() ?? "";

    public (object? created, string? error) NewCustomer(NewPerson p)
    {
        if (ValidatePerson(p) is { } e) return (null, e);
        return CreateWithUser(p, "client", "client", c => AddCustomer(c, p.Name.Trim(), PhoneOf(p)),
            (id, login, pwd) => new { id, name = p.Name.Trim(), phone = PhoneOf(p), login, password = pwd });
    }

    /// <summary>Логин: 3–32 символа — латинские буквы, цифры, точка, дефис, подчёркивание.</summary>
    public static string? ValidateLogin(string? login) =>
        login is not null && System.Text.RegularExpressions.Regex.IsMatch(login.Trim(), "^[A-Za-z0-9._-]{3,32}$") ? null : "INVALID_LOGIN";

    /// <summary>
    /// Самостоятельная регистрация клиента (POST /api/auth/register): клиент и учётная запись создаются в одной
    /// транзакции, логин и пароль обязательны (пароль проверяется так же, как при смене пароля).
    /// Возвращает сессию нового клиента — после регистрации вход выполняется сразу.
    /// </summary>
    public (Session? me, string? error) RegisterClient(RegisterRequest? r)
    {
        if (r is null) return (null, "INVALID_REQUEST");
        if ((CustomerRules.ValidateName(r.Name) ?? CustomerRules.ValidatePhone(r.Phone)) is { } e) return (null, e);
        if (ValidateLogin(r.Login) is { } le) return (null, le);
        if (string.IsNullOrEmpty(r.Password)) return (null, "WEAK_PASSWORD");
        var phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        var (created, err) = NewCustomer(new NewPerson(r.Name, phone, r.Login.Trim(), r.Password));
        if (created is null) return (null, err);
        _log.LogInformation("Зарегистрирован клиент с логином {Login}", r.Login.Trim());
        return (Authenticate(r.Login, r.Password), null);
    }

    public string? UpdateCustomer(Guid id, NewPerson p)
    {
        if (ValidatePerson(p) is { } e) return e;
        using var c = Open();
        return Cmd(c, "UPDATE customers SET name=@n,phone=@p WHERE id=@i", ("n", p.Name.Trim()), ("p", PhoneOf(p)), ("i", id)).ExecuteNonQuery() == 0 ? "CUSTOMER_NOT_FOUND" : null;
    }

    public string? DeleteCustomer(Guid id)
    {
        using var c = Open();
        try
        {
            using var tx = c.BeginTransaction();
            if (Count(c, "SELECT COUNT(*) FROM orders WHERE customer_id=@i", ("i", id)) > 0) return "CUSTOMER_HAS_ORDERS";
            DeleteUsers(c, id, "client");
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
    private static List<Customer> CustomersByIds(NpgsqlConnection c, Guid[] ids)
    {
        var l = new List<Customer>();
        if (ids.Length == 0) return l;
        using var r = Cmd(c, "SELECT id,name,phone FROM customers WHERE id = ANY(@i) ORDER BY name", ("i", ids)).ExecuteReader();
        while (r.Read()) l.Add(new Customer(r.GetGuid(0), r.GetString(1), r.GetString(2)));
        return l;
    }
    public Customer? Customer(Guid id)
    {
        using var c = Open();
        using var r = Cmd(c, "SELECT id,name,phone FROM customers WHERE id=@i", ("i", id)).ExecuteReader();
        return r.Read() ? new Customer(r.GetGuid(0), r.GetString(1), r.GetString(2)) : null;
    }

    // ---------- адреса клиента ----------
    private static Guid AddAddr(NpgsqlConnection c, Guid customerId, string label, string city, string street, string house, string? apt)
    {
        var id = Guid.NewGuid();
        Cmd(c, "INSERT INTO customer_addresses(id,customer_id,label,city,street,house,apartment) VALUES(@i,@c,@l,@ci,@s,@h,@a)",
            ("i", id), ("c", customerId), ("l", label), ("ci", city.Trim()), ("s", street.Trim()), ("h", house.Trim()),
            ("a", string.IsNullOrWhiteSpace(apt) ? null : apt.Trim())).ExecuteNonQuery();
        return id;
    }

    private static List<SavedAddress> Addresses(NpgsqlConnection c, Guid? customerId = null, Guid? addressId = null)
    {
        var ps = new List<(string, object?)>();
        var sql = "SELECT id,customer_id,label,city,street,house,apartment FROM customer_addresses WHERE 1=1";
        if (customerId is not null) { sql += " AND customer_id=@c"; ps.Add(("c", customerId)); }
        if (addressId is not null) { sql += " AND id=@a"; ps.Add(("a", addressId)); }
        using var r = Cmd(c, sql + " ORDER BY label,street", ps.ToArray()).ExecuteReader();
        var l = new List<SavedAddress>();
        while (r.Read()) l.Add(new SavedAddress(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Str(r, 6)));
        return l;
    }

    public List<SavedAddress> Addresses(Guid customerId) { using var c = Open(); return Addresses(c, customerId); }
    public SavedAddress? Address(Guid id) { using var c = Open(); return Addresses(c, addressId: id).FirstOrDefault(); }

    public (SavedAddress? a, string? error) AddAddress(Guid customerId, AddressRequest r)
    {
        if (r is null) return (null, "INVALID_REQUEST");
        if (CustomerRules.ValidateAddress(r.City, r.Street, r.House, r.Apartment, r.Label) is { } e) return (null, e);
        using var c = Open();
        if (Count(c, "SELECT COUNT(*) FROM customers WHERE id=@i", ("i", customerId)) == 0) return (null, "CUSTOMER_NOT_FOUND");
        var id = AddAddr(c, customerId, r.Label?.Trim() ?? "", r.City, r.Street, r.House, r.Apartment);
        return (Addresses(c, customerId, id).First(), null);
    }

    public string? UpdateAddress(Guid id, AddressRequest r)
    {
        if (r is null) return "INVALID_REQUEST";
        if (CustomerRules.ValidateAddress(r.City, r.Street, r.House, r.Apartment, r.Label) is { } e) return e;
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
        if (ValidatePerson(p) is { } e) return (null, e);
        return CreateWithUser(p, "courier", "courier", c => AddCourier(c, p.Name.Trim(), PhoneOf(p)),
            (id, login, pwd) => new { id, name = p.Name.Trim(), phone = PhoneOf(p), state = CourierState.Available, available = true, login, password = pwd });
    }

    public string? UpdateCourier(Guid id, NewPerson p)
    {
        if (ValidatePerson(p) is { } e) return e;
        using var c = Open();
        return Cmd(c, "UPDATE couriers SET name=@n,phone=@p WHERE id=@i", ("n", p.Name.Trim()), ("p", PhoneOf(p)), ("i", id)).ExecuteNonQuery() == 0 ? "COURIER_NOT_FOUND" : null;
    }

    public string? DeleteCourier(Guid id)
    {
        using var c = Open();
        try
        {
            using var tx = c.BeginTransaction();
            if (Count(c, "SELECT COUNT(*) FROM deliveries WHERE courier_id=@i", ("i", id)) > 0) return "COURIER_HAS_ORDERS";
            DeleteUsers(c, id, "courier");
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

    private static List<Courier> CouriersByIds(NpgsqlConnection c, Guid[] ids)
    {
        var l = new List<Courier>();
        if (ids.Length == 0) return l;
        using var r = Cmd(c, "SELECT id,name,phone,state FROM couriers WHERE id = ANY(@i) ORDER BY name", ("i", ids)).ExecuteReader();
        while (r.Read()) l.Add(new Courier(r.GetGuid(0), r.GetString(1), r.GetString(2), Enum.Parse<CourierState>(r.GetString(3))));
        return l;
    }

    public string? SetCourierState(Guid id, CourierState state, bool byOperator)
    {
        if (!Enum.IsDefined(state)) return "INVALID_REQUEST";
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

    private static bool ValidProduct(ProductRequest? r) =>
        r is not null && !string.IsNullOrWhiteSpace(r.Name) && r.Name.Trim().Length <= 100 && Money.IsValidPrice(r.Price);

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

    /// <summary>Экранирует служебные символы шаблона LIKE: «%» и «_» в строке поиска ищутся как обычные символы.</summary>
    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
            ps.Add(("q", "%" + EscapeLike(q.Trim()) + "%"));
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
        using (var h = Cmd(c, "SELECT delivery_id,status,at,comment,actor_id FROM status_history WHERE delivery_id = ANY(@d) ORDER BY id", ("d", deliveryIds)).ExecuteReader())
            while (h.Read()) byDelivery[h.GetGuid(0)].History.Add(new StatusChange(Enum.Parse<DeliveryStatus>(h.GetString(1)), h.GetDateTime(2), Str(h, 3), GuidN(h, 4)));
        using (var h = Cmd(c, "SELECT order_id,status,at,comment,actor_id FROM order_history WHERE order_id = ANY(@o) ORDER BY id", ("o", orderIds)).ExecuteReader())
            while (h.Read()) byOrder[h.GetGuid(0)].OrderHistory.Add(new OrderStatusChange(Enum.Parse<OrderStatus>(h.GetString(1)), h.GetDateTime(2), Str(h, 3), GuidN(h, 4)));
        using (var h = Cmd(c, "SELECT order_id,at,actor_id,actor_name,field,old_value,new_value FROM order_changes WHERE order_id = ANY(@o) ORDER BY id", ("o", orderIds)).ExecuteReader())
            while (h.Read()) byOrder[h.GetGuid(0)].Changes.Add(new OrderChange(h.GetDateTime(1), GuidN(h, 2), Str(h, 3), h.GetString(4), Str(h, 5), Str(h, 6)));
        foreach (var d in list)
        {
            d.Description = string.Join(", ", d.Items.Select(x => $"{x.Name} ×{x.Quantity}"));
            d.Total = Money.FromKopecks(Money.Total(kopecks[d.OrderId]));
        }
        return list;
    }

    public Delivery? Delivery(Guid id) { using var c = Open(); return Deliveries(c, deliveryId: id).FirstOrDefault(); }
    public Delivery? Order(Guid id) { using var c = Open(); return Deliveries(c, orderId: id).FirstOrDefault(); }

    private static void Hist(NpgsqlConnection c, Guid deliveryId, DeliveryStatus s, string? comment, Session? actor = null) =>
        Cmd(c, "INSERT INTO status_history(delivery_id,status,at,comment,actor_id) VALUES(@d,@s,@a,@m,@u)",
            ("d", deliveryId), ("s", s.ToString()), ("a", Now()), ("m", comment), ("u", actor?.UserId)).ExecuteNonQuery();

    private static void OrderHist(NpgsqlConnection c, Guid orderId, OrderStatus s, string? comment, Session? actor = null) =>
        Cmd(c, "INSERT INTO order_history(order_id,status,at,comment,actor_id) VALUES(@o,@s,@a,@m,@u)",
            ("o", orderId), ("s", s.ToString()), ("a", Now()), ("m", comment), ("u", actor?.UserId)).ExecuteNonQuery();

    private static void Change(NpgsqlConnection c, Guid orderId, string field, string? oldValue, string? newValue, Session? actor) =>
        Cmd(c, "INSERT INTO order_changes(order_id,at,actor_id,actor_name,field,old_value,new_value) VALUES(@o,@a,@u,@n,@f,@ov,@nv)",
            ("o", orderId), ("a", Now()), ("u", actor?.UserId), ("n", actor?.Name), ("f", field), ("ov", oldValue), ("nv", newValue)).ExecuteNonQuery();

    private static string ItemsText(IEnumerable<(string Name, int Quantity)> lines) =>
        string.Join(", ", lines.OrderBy(l => l.Name, StringComparer.Ordinal).Select(l => $"{l.Name} ×{l.Quantity}"));

    /// <summary>Журнал изменений заказа (состав, адрес, комментарий), от старых к новым.</summary>
    public List<OrderChange> OrderChanges(Guid orderId)
    {
        using var c = Open();
        return Deliveries(c, orderId: orderId).FirstOrDefault()?.Changes ?? new();
    }

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
            var a = Addresses(c, customerId, aid).FirstOrDefault();
            return a is null ? (null, null, "ADDRESS_NOT_FOUND") : (a.Text, null, null);
        }
        if (inline is null || CustomerRules.ValidateAddress(inline.City, inline.Street, inline.House, inline.Apartment) is { } e) return (null, null, "INVALID_ADDRESS");
        return (inline.ToString(), inline, null);
    }

    /// <summary>Сценарий 1 и 2 из ТЗ: создаёт заказ и доставку, затем пытается назначить свободного курьера.</summary>
    public (Delivery? d, bool courierAssigned, string? error) CreateOrder(CreateOrderRequest r, Session? actor = null)
    {
        if (r is null) return (null, false, "INVALID_REQUEST");
        if (CustomerRules.ValidateComment(r.Comment) is { } ce) return (null, false, ce);
        Guid id;
        var fingerprint = r.RequestId is null ? null : r.Fingerprint();
        using (var c = Open())
        {
            if (Count(c, "SELECT COUNT(*) FROM customers WHERE id=@i", ("i", r.CustomerId)) == 0) return (null, false, "CUSTOMER_NOT_FOUND");
            switch (ExistingByRequest(c, r, fingerprint)) // повторный запрос
            {
                case (_, true): return (null, false, "IDEMPOTENCY_CONFLICT");
                case ({ } dup, _): return (dup, dup.CourierId is not null, null);
            }
            var (lines, e1) = ResolveItems(c, r.Items);
            if (lines is null) return (null, false, e1);
            var (text, inline, e2) = ResolveAddress(c, r.CustomerId, r.AddressId, r.DeliveryAddress);
            if (text is null) return (null, false, e2);

            var orderId = Guid.NewGuid(); id = Guid.NewGuid();
            using var tx = c.BeginTransaction();
            try
            {
                Cmd(c, "INSERT INTO orders(id,customer_id,address,comment,status,created_at,request_id,request_hash) VALUES(@i,@c,@a,@m,@s,@t,@rq,@rh)",
                    ("i", orderId), ("c", r.CustomerId), ("a", text),
                    ("m", string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim()), ("s", nameof(OrderStatus.Created)), ("t", Now()),
                    ("rq", r.RequestId), ("rh", fingerprint)).ExecuteNonQuery();
                InsertItems(c, orderId, lines);
                OrderHist(c, orderId, OrderStatus.Created, "Заказ создан", actor);
                InsertDelivery(c, id, orderId, actor);
                if (r.SaveAddress && inline is not null)
                    AddAddr(c, r.CustomerId, "", inline.City, inline.Street, inline.House, inline.Apartment);
                tx.Commit(); // заказ, позиции, история и доставка сохраняются атомарно
            }
            catch (Exception ex) when (IsPg(ex, PgUniqueViolation) && r.RequestId is not null)
            {
                // параллельный запрос с тем же requestId уже создал заказ: тот же запрос — вернуть его, другой — конфликт
                tx.Rollback();
                using var c2 = Open();
                switch (ExistingByRequest(c2, r, fingerprint))
                {
                    case (_, true): return (null, false, "IDEMPOTENCY_CONFLICT");
                    case ({ } dup2, _): return (dup2, dup2.CourierId is not null, null);
                }
                throw;
            }
            _log.LogInformation("Создан заказ {Order}, доставка {Delivery}", orderId, id);
        }
        // Назначение курьера — отдельный шаг: его сбой не отменяет уже сохранённый заказ (сценарий 2 из ТЗ).
        var assigned = TryAutoAssign(id, actor);
        return (Delivery(id), assigned, null);
    }

    /// <summary>
    /// Заказ, уже созданный этим клиентом с тем же requestId. conflict = true, если отпечаток сохранённого запроса
    /// отличается от текущего (тот же ключ, но другие товары, количество, адрес или комментарий).
    /// </summary>
    private static (Delivery? d, bool conflict) ExistingByRequest(NpgsqlConnection c, CreateOrderRequest r, byte[]? fingerprint)
    {
        if (r.RequestId is not Guid rid) return (null, false);
        Guid oid; byte[]? saved;
        using (var x = Cmd(c, "SELECT id,request_hash FROM orders WHERE customer_id=@c AND request_id=@r", ("c", r.CustomerId), ("r", rid)).ExecuteReader())
        {
            if (!x.Read()) return (null, false);
            oid = x.GetGuid(0); saved = x.IsDBNull(1) ? null : (byte[])x[1];
        }
        // у заказов до миграции 0005 отпечатка нет — сохраняем прежнее поведение и возвращаем заказ
        if (saved is not null && fingerprint is not null && !CryptographicOperations.FixedTimeEquals(saved, fingerprint)) return (null, true);
        return (Deliveries(c, orderId: oid).FirstOrDefault(), false);
    }

    private static void InsertItems(NpgsqlConnection c, Guid orderId, List<(Product p, int q)> lines)
    {
        foreach (var (p, q) in lines)
            Cmd(c, "INSERT INTO order_items(order_id,product_id,name,price_kopecks,quantity) VALUES(@o,@p,@n,@pr,@q)",
                ("o", orderId), ("p", p.Id), ("n", p.Name), ("pr", Money.ToKopecks(p.Price)), ("q", q)).ExecuteNonQuery();
    }

    private static void InsertDelivery(NpgsqlConnection c, Guid id, Guid orderId, Session? actor = null)
    {
        Cmd(c, "INSERT INTO deliveries(id,order_id,status,created_at) VALUES(@i,@o,@s,@t)",
            ("i", id), ("o", orderId), ("s", nameof(DeliveryStatus.Created)), ("t", Now())).ExecuteNonQuery();
        Hist(c, id, DeliveryStatus.Created, "Доставка создана", actor);
    }

    /// <summary>POST /api/deliveries: создание доставки для уже существующего заказа (идемпотентно по orderId).</summary>
    public (Delivery? d, string? error) CreateDelivery(Guid orderId, Session? actor = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        // блокировка заказа: два одновременных запроса не создадут две доставки
        if (Cmd(c, "SELECT 1 FROM orders WHERE id=@i FOR UPDATE", ("i", orderId)).ExecuteScalar() is null) return (null, "ORDER_NOT_FOUND");
        var existing = Deliveries(c, orderId: orderId).FirstOrDefault();
        if (existing is not null) return (existing, "DELIVERY_ALREADY_EXISTS");
        var id = Guid.NewGuid();
        InsertDelivery(c, id, orderId, actor);
        tx.Commit();
        return (Deliveries(c, deliveryId: id).First(), null);
    }

    /// <summary>Изменяет состав, адрес и/или комментарий. Каждое реальное изменение пишется в журнал с автором и временем.</summary>
    public string? UpdateOrder(Guid orderId, UpdateOrderRequest r, Session? actor = null)
    {
        if (r is null) return "INVALID_REQUEST";
        if (CustomerRules.ValidateComment(r.Comment) is { } ce) return ce;
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

        var changed = false;
        if (lines is not null)
        {
            var oldItems = ItemsText(d.Items.Select(i => (i.Name, i.Quantity)));
            var newItems = ItemsText(lines.Select(l => (l.p.Name, l.q)));
            var sameLines = d.Items.OrderBy(i => i.ProductId).Select(i => (i.ProductId, i.Quantity))
                .SequenceEqual(lines.OrderBy(l => l.p.Id).Select(l => (l.p.Id, l.q)));
            if (!sameLines)
            {
                Cmd(c, "DELETE FROM order_items WHERE order_id=@o", ("o", orderId)).ExecuteNonQuery();
                InsertItems(c, orderId, lines);
                Change(c, orderId, "Items", oldItems, newItems, actor);
                changed = true;
            }
        }
        if (addr is not null && addr != d.Address)
        {
            Cmd(c, "UPDATE orders SET address=@a WHERE id=@o", ("a", addr), ("o", orderId)).ExecuteNonQuery();
            Change(c, orderId, "Address", d.Address, addr, actor);
            changed = true;
        }
        if (r.Comment is not null) // null — не менять; пустая строка — очистить
        {
            var newComment = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim();
            if (newComment != d.Comment)
            {
                Cmd(c, "UPDATE orders SET comment=@m WHERE id=@o", ("m", newComment), ("o", orderId)).ExecuteNonQuery();
                Change(c, orderId, "Comment", d.Comment, newComment, actor);
                changed = true;
            }
        }
        if (changed) OrderHist(c, orderId, d.OrderStatus, "Заказ изменён", actor);
        tx.Commit();
        return null;
    }

    // ---------- назначение курьера ----------
    private bool TryAutoAssign(Guid deliveryId, Session? actor)
    {
        try { return Assign(deliveryId, null, actor) is null; }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // заказ уже сохранён, доставка осталась Created: её подхватит фоновое назначение или оператор
            _log.LogError(ex, "Не удалось автоматически назначить курьера для доставки {DeliveryId}, повтор — в фоне", deliveryId);
            return false;
        }
    }

    /// <summary>
    /// Назначает курьера. Без courierId выбирается свободный курьер с наименьшим числом доставок за последние 24 часа
    /// (при равенстве — по имени). Курьер с активной доставкой не назначается, даже если его состояние указано неверно.
    /// </summary>
    public string? Assign(Guid id, Guid? courierId, Session? actor = null)
    {
        Guid? assigned;
        string? error;
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            if (Cmd(c, "SELECT 1 FROM deliveries WHERE id=@i FOR UPDATE", ("i", id)).ExecuteScalar() is null) return "DELIVERY_NOT_FOUND";
            var d = Deliveries(c, deliveryId: id).First();
            (assigned, error) = AssignLocked(c, d, courierId, actor, null);
            if (error is not null) return error;
            tx.Commit();
        }
        _log.LogInformation("Доставка {DeliveryId}: назначен курьер {CourierId}", id, assigned);
        return null;
    }

    private const string ActiveStatuses = "('Assigned','Accepted','InTransit')";
    private const string PgUniqueActiveCourier = "ux_deliveries_active_courier";

    /// <summary>Назначение внутри транзакции, в которой строка доставки уже заблокирована. Ошибка — транзакцию не фиксировать.</summary>
    private static (Guid? courierId, string? error) AssignLocked(NpgsqlConnection c, Delivery d, Guid? courierId, Session? actor, string? note)
    {
        if (!DeliveryRules.AwaitsAssignment(d.Status, d.CourierId is not null))
            return (null, DeliveryRules.IsFinal(d.Status) ? "DELIVERY_ALREADY_COMPLETED" : "DELIVERY_ALREADY_ASSIGNED");
        (Guid Id, string Name, CourierState State, bool HasActive)? cr;
        const string hasActive = "EXISTS(SELECT 1 FROM deliveries a WHERE a.courier_id=couriers.id AND a.status IN " + ActiveStatuses + ")";
        // FOR UPDATE: один курьер не достанется двум доставкам одновременно; SKIP LOCKED — берём следующего свободного
        var sel = courierId is null
            ? "SELECT id,name,state,false FROM couriers WHERE state='Available' AND NOT " + hasActive +
              " ORDER BY (SELECT COUNT(*) FROM deliveries d WHERE d.courier_id=couriers.id AND d.created_at > now() - interval '24 hours'), name FOR UPDATE SKIP LOCKED LIMIT 1"
            : "SELECT id,name,state," + hasActive + " FROM couriers WHERE id=@c FOR UPDATE";
        using (var r = Cmd(c, sel, courierId is null ? Array.Empty<(string, object?)>() : new (string, object?)[] { ("c", courierId) }).ExecuteReader())
            cr = r.Read() ? (r.GetGuid(0), r.GetString(1), Enum.Parse<CourierState>(r.GetString(2)), r.GetBoolean(3)) : null;
        if (cr is null) return (null, courierId is null ? "COURIER_NOT_AVAILABLE" : "COURIER_NOT_FOUND");
        if (!CourierRules.CanAssign(cr.Value.State)) return (null, "COURIER_NOT_AVAILABLE");
        if (cr.Value.HasActive) return (null, "COURIER_BUSY"); // состояние «Свободен», но активная доставка уже есть
        Cmd(c, "UPDATE couriers SET state='Busy' WHERE id=@i", ("i", cr.Value.Id)).ExecuteNonQuery();
        try
        {
            Cmd(c, "UPDATE deliveries SET courier_id=@c,status=@s WHERE id=@i", ("c", cr.Value.Id), ("s", nameof(DeliveryStatus.Assigned)), ("i", d.DeliveryId)).ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState == PgUniqueViolation && ex.ConstraintName == PgUniqueActiveCourier)
        {
            return (null, "COURIER_BUSY"); // последняя линия защиты: уникальный индекс активных доставок курьера
        }
        Hist(c, d.DeliveryId, DeliveryStatus.Assigned, "Назначен курьер " + cr.Value.Name + (note is null ? "" : $" ({note})"), actor);
        SetOrderStatus(c, d.OrderId, d.OrderStatus, DeliveryRules.ToOrderStatus(DeliveryStatus.Assigned), null, actor);
        return (cr.Value.Id, null);
    }

    /// <summary>Итог одного прохода фонового назначения.</summary>
    public readonly record struct PendingAssignmentResult(int Assigned, bool NoFreeCouriers);

    /// <summary>
    /// Фоновое назначение: по одной доставке в транзакции берёт самую старую доставку в статусе Created без курьера
    /// (FOR UPDATE SKIP LOCKED — несколько экземпляров API и ручное назначение не мешают друг другу) и назначает
    /// наименее загруженного свободного курьера. Останавливается, когда ожидающих доставок или свободных курьеров нет.
    /// Ошибки БД пробрасываются вызывающему (фоновой службе), уже назначенные доставки остаются назначенными.
    /// </summary>
    public PendingAssignmentResult AssignPendingDeliveries(int batchSize, CancellationToken ct = default)
    {
        var assigned = 0;
        // доставки, которые в этом проходе назначить не удалось из-за ошибки данных: они не должны
        // каждый раз оказываться «самыми старыми» и останавливать назначение остальных
        var skipped = new List<Guid>();
        PostgresException? lastError = null;
        for (var i = 0; i < batchSize && !ct.IsCancellationRequested; i++)
        {
            Guid deliveryId, courierId;
            using (var c = Open())
            using (var tx = c.BeginTransaction())
            {
                if (Cmd(c, @"SELECT id FROM deliveries WHERE status='Created' AND courier_id IS NULL AND NOT (id = ANY(@skip))
                             ORDER BY created_at, id FOR UPDATE SKIP LOCKED LIMIT 1", ("skip", skipped.ToArray())).ExecuteScalar() is not Guid id)
                    return Done();
                Guid? cid; string? e;
                try
                {
                    var d = Deliveries(c, deliveryId: id).First();
                    (cid, e) = AssignLocked(c, d, null, null, "повторное автоназначение");
                }
                catch (PostgresException ex) when (!IsTransient(ex))
                {
                    // ошибка конкретной доставки (ограничение, триггер): пропускаем её, остальные назначаем дальше
                    _log.LogError(ex, "Фоновое назначение: доставка {DeliveryId} пропущена из-за ошибки БД", id);
                    skipped.Add(id);
                    lastError = ex;
                    continue;
                }
                if (e is "COURIER_NOT_AVAILABLE" or "COURIER_BUSY") return new(assigned, true);
                if (e is not null)
                {
                    _log.LogWarning("Фоновое назначение: доставка {DeliveryId} пропущена ({ErrorCode})", id, e);
                    skipped.Add(id);
                    continue;
                }
                tx.Commit();
                deliveryId = id; courierId = cid!.Value;
            }
            assigned++;
            _log.LogInformation("Фоновое назначение: доставка {DeliveryId} → курьер {CourierId}", deliveryId, courierId);
        }
        return Done();

        // ни одна доставка не назначена, и были ошибки БД — сбой общий (например, таблицы курьеров):
        // пробрасываем его, чтобы фоновая служба увеличила паузу между попытками
        PendingAssignmentResult Done()
        {
            if (assigned == 0 && lastError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(lastError);
            return new(assigned, false);
        }
    }

    private static void SetOrderStatus(NpgsqlConnection c, Guid orderId, OrderStatus from, OrderStatus to, string? comment, Session? actor = null)
    {
        if (from == to) return;
        Cmd(c, "UPDATE orders SET status=@s WHERE id=@o", ("s", to.ToString()), ("o", orderId)).ExecuteNonQuery();
        OrderHist(c, orderId, to, comment, actor);
    }

    // ---------- смена статуса ----------
    private static readonly DeliveryStatus[] CourierTargets = { DeliveryStatus.Accepted, DeliveryStatus.InTransit, DeliveryStatus.Delivered, DeliveryStatus.Failed };

    public string? ChangeStatus(Guid id, DeliveryStatus s, string? comment, Session? actor = null)
    {
        if (!Enum.IsDefined(s)) return "INVALID_REQUEST";
        if (CustomerRules.ValidateComment(comment) is { } ce) return ce;
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
        Hist(c, id, s, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), actor);
        SetOrderStatus(c, d.OrderId, d.OrderStatus, DeliveryRules.ToOrderStatus(s), s == DeliveryStatus.Failed ? "Доставка не выполнена: " + comment!.Trim() : null, actor);
        if (final && d.CourierId is Guid cid)
            Cmd(c, "UPDATE couriers SET state='Available' WHERE id=@i AND state='Busy'", ("i", cid)).ExecuteNonQuery();
        tx.Commit();
        _log.LogInformation("Доставка {Delivery}: {From} → {To}", id, d.Status, s);
        return null;
    }

    /// <summary>Повторная доставка после неудачной: доставка возвращается в Created (история сохраняется), курьер подбирается заново.</summary>
    public string? Retry(Guid id, Session? actor)
    {
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            if (Cmd(c, "SELECT 1 FROM deliveries WHERE id=@i FOR UPDATE", ("i", id)).ExecuteScalar() is null) return "DELIVERY_NOT_FOUND";
            var d = Deliveries(c, deliveryId: id).First();
            if (!DeliveryRules.CanRetry(d.Status)) return "DELIVERY_NOT_FAILED";
            Cmd(c, "UPDATE deliveries SET status='Created',courier_id=NULL,started_at=NULL,finished_at=NULL,failure_reason=NULL WHERE id=@i", ("i", id)).ExecuteNonQuery();
            Hist(c, id, DeliveryStatus.Created, "Повторная доставка", actor);
            SetOrderStatus(c, d.OrderId, d.OrderStatus, DeliveryRules.ToOrderStatus(DeliveryStatus.Created), "Повторная доставка", actor);
            tx.Commit();
        }
        _log.LogInformation("Доставка {Delivery}: повторная попытка", id);
        TryAutoAssign(id, actor);
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
        // клиент и курьер получают из БД только свои доставки, а не все заказы системы с последующей фильтрацией
        var deliveries = me?.Role switch
        {
            "client" => Deliveries(c, customerId: me.RefId ?? Guid.Empty),
            "courier" => Deliveries(c, courierId: me.RefId ?? Guid.Empty),
            _ => Deliveries(c)
        };
        List<Customer> customers;
        List<Courier> couriers;
        List<SavedAddress> addresses;
        if (me?.Role == "client")
        {
            customers = CustomersByIds(c, me.RefId is Guid cid ? new[] { cid } : Array.Empty<Guid>());
            addresses = me.RefId is Guid aid ? Addresses(c, aid) : new();
            couriers = CouriersByIds(c, deliveries.Where(d => d.CourierId is not null).Select(d => d.CourierId!.Value).Distinct().ToArray());
        }
        else if (me?.Role == "courier")
        {
            customers = CustomersByIds(c, deliveries.Select(d => d.CustomerId).Distinct().ToArray());
            couriers = CouriersByIds(c, me.RefId is Guid rid ? new[] { rid } : Array.Empty<Guid>());
            addresses = new();
        }
        else
        {
            customers = Customers(c);
            couriers = Couriers(c);
            addresses = Addresses(c);
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
