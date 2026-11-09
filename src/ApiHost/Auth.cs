using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ApiHost;

/// <summary>Сессии по токену (Bearer). Хранятся в памяти, живут 12 часов.</summary>
public class AuthService
{
    private readonly ConcurrentDictionary<string, (Session s, DateTime exp)> _sessions = new();

    public string Start(Session s)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _sessions[token] = (s, DateTime.UtcNow.AddHours(12));
        return token;
    }

    public void End(string token) => _sessions.TryRemove(token, out _);

    public static string? Token(HttpRequest r)
    {
        var h = r.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    public Session? Get(HttpRequest r)
    {
        var t = Token(r);
        if (t is null || !_sessions.TryGetValue(t, out var v)) return null;
        if (v.exp < DateTime.UtcNow) { _sessions.TryRemove(t, out _); return null; }
        return v.s;
    }
}
