using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ApiHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Integration.Tests;

/// <summary>Учётные записи: стойкие пароли новых пользователей, отзыв сессий, ограничение подбора пароля.</summary>
public class AccountSecurityTests : ApiTests
{
    private static string TokenOf(HttpClient c) => c.DefaultRequestHeaders.Authorization!.Parameter!;

    [Fact]
    public async Task DeletedCustomer_TokenStopsWorking()
    {
        var op = await LoginAsync("operator");
        var created = await Body(await op.PostAsync("/api/customers", Json("""{"name":"Временный","login":"temp1","password":"Temp-pass-91"}""")));
        var client = await LoginAsync("temp1", "Temp-pass-91");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await op.DeleteAsync($"/api/customers/{created["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(0, F.Db.Scalar<long>("SELECT COUNT(*) FROM sessions s LEFT JOIN users u ON u.id=s.user_id WHERE u.id IS NULL"));
    }

    [Fact]
    public async Task DeletedCourier_TokenStopsWorking()
    {
        var op = await LoginAsync("operator");
        var created = await Body(await op.PostAsync("/api/couriers", Json("""{"name":"Новый Курьер","login":"cour1","password":"Cour-pass-73"}""")));
        var courier = await LoginAsync("cour1", "Cour-pass-73");
        Assert.Equal(HttpStatusCode.OK, (await op.DeleteAsync($"/api/couriers/{created["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await courier.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task GeneratedPassword_IsLongEnough_AndWorks()
    {
        var op = await LoginAsync("operator");
        var created = await Body(await op.PostAsync("/api/couriers", Json("""{"name":"Без пароля"}""")));
        var login = created["login"]!.ToString();
        var password = created["password"]!.ToString();
        Assert.True(password.Length >= Store.MinPasswordLength, password);
        Assert.Matches("^[a-zA-Z2-9]{12}$", password);
        await LoginAsync(login, password);
    }

    [Theory]
    [InlineData("""{"name":"А","login":"weak1","password":"123"}""")]
    [InlineData("""{"name":"Б","login":"weak2-login","password":"weak2-login"}""")]
    [InlineData("""{"name":"В","login":"weak3","password":"password"}""")]
    public async Task NewUser_WithWeakPassword_IsRejected(string body)
    {
        var op = await LoginAsync("operator");
        var r = await op.PostAsync("/api/customers", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("WEAK_PASSWORD", await Code(r));
        Assert.Equal(2, F.Db.Scalar<long>("SELECT COUNT(*) FROM customers"));          // ничего не создано
    }

    [Fact]
    public async Task ChangePassword_ToLogin_IsRejected()
    {
        var c = await LoginAsync("operator");
        var r = await c.PostAsync("/api/auth/password", Json("""{"oldPassword":"operator","newPassword":"OPERATOR"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("WEAK_PASSWORD", await Code(r));
    }

    [Fact]
    public async Task RepeatedWrongPasswords_LockLoginTemporarily()
    {
        var anon = F.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/auth/login", Json("""{"login":"ivan","password":"guess"}"""))).StatusCode);

        var locked = await anon.PostAsync("/api/auth/login", Json("""{"login":"IVAN","password":"ivan"}""")); // даже верный пароль
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.Equal("TOO_MANY_ATTEMPTS", await Code(locked));
        await LoginAsync("maria");                                                          // другие логины не затронуты
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsFailureCounter()
    {
        var anon = F.CreateClient();
        for (var i = 0; i < 4; i++) await anon.PostAsync("/api/auth/login", Json("""{"login":"ivan","password":"guess"}"""));
        await LoginAsync("ivan");
        for (var i = 0; i < 4; i++) await anon.PostAsync("/api/auth/login", Json("""{"login":"ivan","password":"guess"}"""));
        await LoginAsync("ivan");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Throttle_UnlocksAfterWindow()
    {
        var clock = new Clock();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:MaxLoginFailures"] = "3", ["Security:LoginLockoutSeconds"] = "60" }).Build();
        var t = new LoginThrottle(cfg, clock);
        for (var i = 0; i < 3; i++) t.Failed("ivan");
        Assert.True(t.IsLocked("ivan"));
        Assert.False(t.IsLocked("maria"));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.False(t.IsLocked("ivan"));
    }
}
