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
