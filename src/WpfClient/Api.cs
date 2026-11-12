using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WpfClient;

public class ApiException : Exception
{
    public string Code { get; }
    public ApiException(string code, string message) : base(message) => Code = code;
}

/// <summary>Клиент REST API сервера ApiHost: Bearer-токен, JSON, единый формат ошибок.</summary>
public class Api
{
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public string? Role { get; private set; }
    public Guid? RefId { get; private set; }
    public string UserName { get; private set; } = "";

    /// <summary>
    /// Адрес сервера из поля ввода: «localhost:8080» дополняется до «http://localhost:8080/».
    /// Допустимы только http и https; иначе — UriFormatException с понятным текстом.
    /// </summary>
    public static Uri ServerUri(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) throw new UriFormatException("Укажите адрес сервера, например http://localhost:8080");
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || u.Host.Length == 0)
            throw new UriFormatException("Некорректный адрес сервера. Пример: http://localhost:8080");
        return u;
    }

    public void SetServer(string url)
    {
        // HttpClient нельзя менять после первого запроса (повторный вход) — создаём заново.
        _http.Dispose();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), BaseAddress = ServerUri(url) };
        Role = null; RefId = null; UserName = "";
    }

    public async Task LoginAsync(string login, string password)
    {
        var r = await SendAsync(HttpMethod.Post, "api/auth/login", new { login, password });
        ApplySession(r!, login);
    }

    /// <summary>Самостоятельная регистрация клиента; после неё сразу выполняется вход.</summary>
    public async Task RegisterAsync(string name, string? phone, string login, string password)
    {
        var r = await SendAsync(HttpMethod.Post, "api/auth/register", new { name, phone, login, password });
        ApplySession(r!, login);
    }

    private void ApplySession(JsonNode r, string login)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", r["token"]!.GetValue<string>());
        Role = r["role"]!.GetValue<string>();
        RefId = Guid.TryParse(r["refId"]?.ToString(), out var g) ? g : null;
        UserName = r["name"]?.ToString() ?? login;
    }

    public async Task LogoutAsync()
    {
        try { await SendAsync(HttpMethod.Post, "api/auth/logout", null); } catch { }
        _http.DefaultRequestHeaders.Authorization = null;
    }

    public Task<JsonNode?> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);
    public Task<JsonNode?> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });
    public Task<JsonNode?> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);
    public Task<JsonNode?> PatchAsync(string path, object body) => SendAsync(HttpMethod.Patch, path, body);
    public Task<JsonNode?> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

    private async Task<JsonNode?> SendAsync(HttpMethod m, string path, object? body)
    {
        using var req = new HttpRequestMessage(m, path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new ApiException("NO_CONNECTION", "Нет связи с сервером. Проверьте адрес и что ApiHost запущен."); }
        var text = await resp.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(text)) { try { json = JsonNode.Parse(text); } catch { } }
        if (!resp.IsSuccessStatusCode)
            throw new ApiException(json?["errorCode"]?.ToString() ?? "ERROR", json?["message"]?.ToString() ?? $"Ошибка {(int)resp.StatusCode}");
        return json;
    }
}
