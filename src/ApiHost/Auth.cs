using System.Security.Cryptography;
using System.Text;

namespace ApiHost;

/// <summary>Сессии по токену (Bearer). Хранятся в PostgreSQL (в виде SHA-256 токена), живут 12 часов и переживают перезапуск сервера.</summary>
public class AuthService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly Store _store;

    public AuthService(Store store) => _store = store;

    internal static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public string Start(Session s)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _store.SaveSession(HashToken(token), s, DateTime.UtcNow + Lifetime);
        return token;
    }

    public void End(string token) => _store.DeleteSession(HashToken(token));

    public static string? Token(HttpRequest r)
    {
        var h = r.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    public Session? Get(HttpRequest r) => Token(r) is { Length: > 0 } t ? _store.LoadSession(HashToken(t)) : null;
}

/// <summary>
/// Ограничение подбора пароля: после MaxFailures неудачных попыток входа под одним логином с одного адреса
/// в течение окна вход под этим логином с этого адреса временно отклоняется кодом TOO_MANY_ATTEMPTS (HTTP 429).
/// Ключ «логин + адрес» не даёт постороннему заблокировать чужую учётную запись со своего адреса.
/// Счётчики хранятся в PostgreSQL (таблица rate_limits): общие для экземпляров API и переживают перезапуск.
/// </summary>
public class LoginThrottle
{
    private readonly Store _store;
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;

    public LoginThrottle(Store store, IConfiguration cfg, TimeProvider? time = null)
    {
        _store = store;
        _maxFailures = Math.Max(1, cfg.GetValue("Security:MaxLoginFailures", 5));
        _window = TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Security:LoginLockoutSeconds", 300)));
        _time = time ?? TimeProvider.System;
    }

    private static string Key(string? login, string? address) =>
        "login:" + (login ?? "").Trim().ToLowerInvariant() + "|" + (address ?? "-");

    public bool IsLocked(string? login, string? address) => _store.RateLimitCount(Key(login, address), _window, _time.GetUtcNow()) >= _maxFailures;

    public void Failed(string? login, string? address) => _store.RateLimitHit(Key(login, address), _window, _time.GetUtcNow());

    public void Succeeded(string? login, string? address) => _store.RateLimitReset(Key(login, address));
}

/// <summary>
/// Ограничение самостоятельной регистрации: не больше MaxRegistrationsPerHour попыток в час с одного адреса
/// (защита от массового создания учётных записей и перебора занятых логинов). Счётчик хранится в PostgreSQL.
/// </summary>
public class RegistrationThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly Store _store;
    private readonly int _max;
    private readonly TimeProvider _time;

    public RegistrationThrottle(Store store, IConfiguration cfg, TimeProvider? time = null)
    {
        _store = store;
        _max = Math.Max(1, cfg.GetValue("Security:MaxRegistrationsPerHour", 20));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Учитывает попытку и возвращает false, если лимит для адреса уже исчерпан.</summary>
    public bool TryAcquire(string? address) => _store.RateLimitHit("register:" + (address ?? "-"), Window, _time.GetUtcNow()) <= _max;
}
