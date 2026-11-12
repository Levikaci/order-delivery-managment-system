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
/// Ограничение подбора пароля: после MaxFailures неудачных попыток входа под одним логином в течение Window
/// вход под этим логином временно отклоняется кодом TOO_MANY_ATTEMPTS (HTTP 429). Счётчик хранится в памяти экземпляра API.
/// </summary>
public class LoginThrottle
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> _failures = new(StringComparer.OrdinalIgnoreCase);

    public LoginThrottle(IConfiguration cfg, TimeProvider? time = null)
    {
        _maxFailures = Math.Max(1, cfg.GetValue("Security:MaxLoginFailures", 5));
        _window = TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Security:LoginLockoutSeconds", 300)));
        _time = time ?? TimeProvider.System;
    }

    private static string Key(string? login) => (login ?? "").Trim();

    public bool IsLocked(string? login)
    {
        if (!_failures.TryGetValue(Key(login), out var f)) return false;
        if (_time.GetUtcNow() - f.Since >= _window) { _failures.TryRemove(Key(login), out _); return false; }
        return f.Failures >= _maxFailures;
    }

    public void Failed(string? login)
    {
        var now = _time.GetUtcNow();
        _failures.AddOrUpdate(Key(login), _ => (1, now), (_, f) => now - f.Since >= _window ? (1, now) : (f.Failures + 1, f.Since));
        if (_failures.Count > 10_000) // защита памяти от перебора несуществующих логинов
            foreach (var (k, v) in _failures)
                if (now - v.Since >= _window) _failures.TryRemove(k, out _);
    }

    public void Succeeded(string? login) => _failures.TryRemove(Key(login), out _);
}

/// <summary>
/// Ограничение самостоятельной регистрации: не больше MaxRegistrationsPerHour попыток в час с одного адреса
/// (защита от массового создания учётных записей и перебора занятых логинов). Счётчик хранится в памяти экземпляра API.
/// </summary>
public class RegistrationThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly int _max;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _attempts = new();

    public RegistrationThrottle(IConfiguration cfg, TimeProvider? time = null)
    {
        _max = Math.Max(1, cfg.GetValue("Security:MaxRegistrationsPerHour", 20));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Учитывает попытку и возвращает false, если лимит для адреса уже исчерпан.</summary>
    public bool TryAcquire(string? address)
    {
        var now = _time.GetUtcNow();
        var v = _attempts.AddOrUpdate(address ?? "", _ => (1, now), (_, f) => now - f.Since >= Window ? (1, now) : (f.Count + 1, f.Since));
        if (_attempts.Count > 10_000)
            foreach (var (k, x) in _attempts)
                if (now - x.Since >= Window) _attempts.TryRemove(k, out _);
        return v.Count <= _max;
    }
}
