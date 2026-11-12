using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Integration.Tests;

/// <summary>Самостоятельная регистрация клиента: POST /api/auth/register.</summary>
public sealed class RegistrationTests : IDisposable
{
    private readonly List<ApiFactory> _factories = new();
    private ApiFactory New(Dictionary<string, string>? settings = null) { var f = new ApiFactory(settings: settings); _factories.Add(f); return f; }
    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonNode> Body(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonNode>())!;
    private static Task<HttpResponseMessage> Register(HttpClient c, string login, string password = "Strong-pass-42", string name = "Анна Новикова") =>
        c.PostAsync("/api/auth/register", Json($$"""{"name":"{{name}}","phone":"+7 900 123-45-67","login":"{{login}}","password":"{{password}}"}"""));

    [Fact]
    public async Task NewClient_CanRegister_AndImmediatelyWork()
    {
        var f = New();
        var anon = f.CreateClient();
        var r = await Register(anon, "anna.n");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var b = await Body(r);
        Assert.Equal("client", b["role"]!.ToString());
        Assert.Equal("Анна Новикова", b["name"]!.ToString());

        var me = f.CreateClient();
        me.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", b["token"]!.ToString());
        var state = await Body(await me.GetAsync("/api/state"));
        Assert.Equal(b["refId"]!.ToString(), state["customers"]![0]!["id"]!.ToString());     // видит только себя
        Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync("/api/customers")).StatusCode); // прав оператора нет

        // можно оформить заказ сразу после регистрации
        var pizza = state["products"]!.AsArray().First(p => p!["name"]!.ToString().Contains("Пицца"))!["id"]!.ToString();
        var order = await me.PostAsync("/api/orders", Json("{\"customerId\":\"" + b["refId"] + "\",\"items\":[{\"productId\":\"" + pizza +
            "\",\"quantity\":1}],\"deliveryAddress\":{\"city\":\"Волгоград\",\"street\":\"Ленина\",\"house\":\"1\"}}"));
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);

        // и войти заново по логину и паролю
        var login = await anon.PostAsync("/api/auth/login", Json("""{"login":"ANNA.N","password":"Strong-pass-42"}"""));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Theory]
    [InlineData("ivan", "Strong-pass-42", "", HttpStatusCode.BadRequest, "INVALID_NAME")]
    [InlineData("IVAN", "Strong-pass-42", "Иван", HttpStatusCode.Conflict, "LOGIN_TAKEN")]      // регистр не важен
    [InlineData("ab", "Strong-pass-42", "Иван", HttpStatusCode.BadRequest, "INVALID_LOGIN")]
    [InlineData("иван", "Strong-pass-42", "Иван", HttpStatusCode.BadRequest, "INVALID_LOGIN")]
    [InlineData("new.user", "123", "Иван", HttpStatusCode.BadRequest, "WEAK_PASSWORD")]
    [InlineData("new.user", "new.user", "Иван", HttpStatusCode.BadRequest, "WEAK_PASSWORD")]
    [InlineData("new.user", "", "Иван", HttpStatusCode.BadRequest, "WEAK_PASSWORD")]
    public async Task InvalidRegistration_IsRejected_AndCreatesNothing(string login, string password, string name, HttpStatusCode status, string code)
    {
        var f = New();
        var r = await Register(f.CreateClient(), login, password, name);
        Assert.Equal(status, r.StatusCode);
        Assert.Equal(code, (await Body(r))["errorCode"]!.ToString());
        Assert.Equal(2, f.Db.Scalar<long>("SELECT COUNT(*) FROM customers"));
    }

    [Fact]
    public async Task Registration_CanBeDisabled()
    {
        var f = New(new() { ["Security:AllowRegistration"] = "false" });
        var r = await Register(f.CreateClient(), "anna.n");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("REGISTRATION_DISABLED", (await Body(r))["errorCode"]!.ToString());
        Assert.Equal(2, f.Db.Scalar<long>("SELECT COUNT(*) FROM customers"));
    }

    [Fact]
    public async Task Registration_IsRateLimited()
    {
        var f = New(new() { ["Security:MaxRegistrationsPerHour"] = "2" });
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Register(c, "user.one")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Register(c, "user.two")).StatusCode);
        var r = await Register(c, "user.three");
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Equal("TOO_MANY_REGISTRATIONS", (await Body(r))["errorCode"]!.ToString());
    }
}
