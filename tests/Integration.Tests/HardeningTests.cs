using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ApiHost;
using Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Integration.Tests;

/// <summary>Сессии в БД, смена пароля, боевой запуск без демо-данных.</summary>
public class SessionAndPasswordTests : ApiTests
{
    private static string TokenOf(HttpClient c) => c.DefaultRequestHeaders.Authorization!.Parameter!;

    [Fact]
    public async Task Session_SurvivesServerRestart()
    {
        var c = await LoginAsync("operator");
        using var restarted = new ApiFactory(F.Db);              // «новый процесс» с той же базой
        var c2 = restarted.CreateClient();
        c2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenOf(c));
        Assert.Equal(HttpStatusCode.OK, (await c2.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_InvalidatesToken_AndDatabaseKeepsOnlyHash()
    {
        var c = await LoginAsync("ivan");
        var token = TokenOf(c);
        Assert.Equal(0, F.Db.Scalar<long>($"SELECT COUNT(*) FROM sessions WHERE encode(token_hash,'hex') ILIKE '{token}'"));
        Assert.Equal(1, F.Db.Scalar<long>("SELECT COUNT(*) FROM sessions"));
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(0, F.Db.Scalar<long>("SELECT COUNT(*) FROM sessions"));
    }

    [Fact]
    public async Task ExpiredSession_IsRejected()
    {
        var c = await LoginAsync("ivan");
        F.Db.Sql("UPDATE sessions SET expires_at = now() - interval '1 minute'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WorksAndEndsOtherSessions()
    {
        var current = await LoginAsync("ivan");
        var other = await LoginAsync("ivan");
        var r = await current.PostAsync("/api/auth/password", Json("""{"oldPassword":"ivan","newPassword":"new-secret-1"}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/auth/me")).StatusCode);          // текущая сессия остаётся
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/auth/me")).StatusCode);   // остальные завершены
        var old = await F.CreateClient().PostAsync("/api/auth/login", Json("""{"login":"ivan","password":"ivan"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        await LoginAsync("ivan", "new-secret-1");
    }

    [Theory]
    [InlineData("ivan", "short", HttpStatusCode.BadRequest, "WEAK_PASSWORD")]
    [InlineData("ivan", "ivan", HttpStatusCode.BadRequest, "WEAK_PASSWORD")]       // совпадает со старым
    [InlineData("wrong", "new-secret-1", HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS")]
    public async Task ChangePassword_Rejects(string oldPwd, string newPwd, HttpStatusCode status, string code)
    {
        var c = await LoginAsync("ivan");
        var r = await c.PostAsync("/api/auth/password", Json($$"""{"oldPassword":"{{oldPwd}}","newPassword":"{{newPwd}}"}"""));
        Assert.Equal(status, r.StatusCode);
        Assert.Equal(code, await Code(r));
        await LoginAsync("ivan");                                                       // пароль не изменился
    }

    [Fact]
    public async Task ChangePassword_RequiresLogin()
    {
        var r = await F.CreateClient().PostAsync("/api/auth/password", Json("""{"oldPassword":"a","newPassword":"bbbbbbbbb"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

public class ProductionSeedTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private Store Open(bool demo, string? password) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Default"] = _db.ConnectionString, ["Seed:Demo"] = demo.ToString(), ["Seed:OperatorPassword"] = password
    }).Build(), NullLogger<Store>.Instance);

    [Fact]
    public void WithoutDemo_OnlyOperatorWithConfiguredPassword_IsCreated()
    {
        using var s = Open(false, "Str0ng-pass");
        Assert.Empty(s.Customers());
        Assert.Empty(s.Couriers());
        Assert.Empty(s.Products());
        Assert.NotNull(s.Authenticate("operator", "Str0ng-pass"));
        Assert.Null(s.Authenticate("operator", "operator"));
        Assert.Null(s.Authenticate("ivan", "ivan"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public void WithoutDemo_RequiresStrongOperatorPassword(string? password)
    {
        Assert.Throws<InvalidOperationException>(() => Open(false, password));
        Assert.Equal(0, _db.Scalar<long>("SELECT COUNT(*) FROM users"));               // ничего не создано
    }

    [Fact]
    public void ExistingDatabase_IsNotReseeded()
    {
        using (var s = Open(true, null)) Assert.NotEmpty(s.Customers());
        using var again = Open(false, null);                                              // база не пуста — пароль не нужен
        Assert.NotEmpty(again.Customers());
        Assert.NotNull(again.Authenticate("operator", "operator"));
    }
}

/// <summary>Автоподбор курьера с балансировкой и повторная доставка после неудачи.</summary>
public class AssignmentAndRetryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Store _s;
    private readonly Guid _ivan;
    private readonly Product _pizza;
    private static readonly AddressDto Addr = new("Волгоград", "Мира", "15", "24");
    private static readonly Session Op = new("operator", null, "Оператор");

    public AssignmentAndRetryTests()
    {
        _s = _db.OpenStore();
        _ivan = _s.Customers().First(c => c.Name.StartsWith("Иван")).Id;
        _pizza = _s.Products().First(p => p.Name.Contains("Пицца"));
    }
    public void Dispose() { _s.Dispose(); _db.Dispose(); }

    private Delivery NewOrder() => _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 1) }, Addr)).d!;
    private string CourierName(Guid deliveryId) => _s.Couriers().First(c => c.Id == _s.Delivery(deliveryId)!.CourierId).Name;

    private void Finish(Guid id, DeliveryStatus last = DeliveryStatus.Delivered)
    {
        foreach (var st in new[] { DeliveryStatus.Accepted, DeliveryStatus.InTransit })
            Assert.Null(_s.ChangeStatus(id, st, null, Op));
        Assert.Null(_s.ChangeStatus(id, last, last == DeliveryStatus.Failed ? "Клиента нет дома" : null, Op));
    }

    [Fact]
    public void AutoAssign_SpreadsDeliveriesBetweenCouriers()
    {
        var names = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var d = NewOrder();
            names.Add(CourierName(d.DeliveryId));
            Finish(d.DeliveryId);                                    // курьер снова свободен — но следующим берётся наименее загруженный
        }
        // раньше всегда выбирался «Алексей» (первый по имени); теперь: Алексей, Дмитрий, Ольга, затем круг повторяется
        Assert.Equal(new[] { "Алексей Курьеров", "Дмитрий Быстров", "Ольга Скорая", "Алексей Курьеров" }, names);
    }

    [Fact]
    public void AutoAssign_SkipsBusyCouriers()
    {
        var a = NewOrder(); var b = NewOrder();
        Assert.NotEqual(CourierName(a.DeliveryId), CourierName(b.DeliveryId));
    }

    [Fact]
    public void Retry_AfterFailure_ReturnsDeliveryToQueueAndAssignsCourier()
    {
        var d = NewOrder();
        Finish(d.DeliveryId, DeliveryStatus.Failed);
        Assert.Equal(OrderStatus.DeliveryFailed, _s.Delivery(d.DeliveryId)!.OrderStatus);

        Assert.Null(_s.Retry(d.DeliveryId, Op));

        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal(DeliveryStatus.Assigned, x.Status);               // курьер подобран заново
        Assert.Equal(OrderStatus.Confirmed, x.OrderStatus);
        Assert.Null(x.FailureReason);
        Assert.Null(x.FinishedAt);
        Assert.Contains(x.History, h => h.Status == DeliveryStatus.Failed);            // история неудачи сохранена
        Assert.Contains(x.History, h => h.Comment == "Повторная доставка");
        Finish(d.DeliveryId);                                           // и доставку можно довести до конца
        Assert.Equal(OrderStatus.Delivered, _s.Delivery(d.DeliveryId)!.OrderStatus);
    }

    [Fact]
    public void Retry_WithoutFreeCouriers_KeepsDeliveryCreated()
    {
        var d = NewOrder();
        Finish(d.DeliveryId, DeliveryStatus.Failed);
        foreach (var c in _s.Couriers()) _s.SetCourierState(c.Id, CourierState.Offline, true);
        Assert.Null(_s.Retry(d.DeliveryId, Op));
        Assert.Equal(DeliveryStatus.Created, _s.Delivery(d.DeliveryId)!.Status);
        Assert.Equal(OrderStatus.Created, _s.Delivery(d.DeliveryId)!.OrderStatus);
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Created")]
    [InlineData("Cancelled")]
    public void Retry_OnlyForFailedDeliveries(string state)
    {
        var d = NewOrder();
        if (state == "Delivered") Finish(d.DeliveryId);
        if (state == "Cancelled") Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Op));
        if (state == "Created") { _db.Sql("UPDATE deliveries SET status='Created',courier_id=NULL"); }
        Assert.Equal("DELIVERY_NOT_FAILED", _s.Retry(d.DeliveryId, Op));
    }

    [Fact]
    public void Retry_UnknownDelivery() => Assert.Equal("DELIVERY_NOT_FOUND", _s.Retry(Guid.NewGuid(), Op));
}

public class RetryApiTests : ApiTests
{
    [Fact]
    public async Task Retry_IsOperatorOnly_AndWorksOverHttp()
    {
        var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        var ivan = await LoginAsync("ivan");
        var order = await CreateOrderAs(ivan, ivanId, pizza);
        var deliveryId = order["deliveryId"]!.ToString();
        var courierToken = await LoginAsync(order["courierId"] is { } ? CourierLoginFor(await Body(await op.GetAsync("/api/couriers"))!, order["courierId"]!.ToString()) : "alexey");
        foreach (var st in new[] { "Accepted", "InTransit" })
            Assert.Equal(HttpStatusCode.OK, (await courierToken.PatchAsync($"/api/deliveries/{deliveryId}/status", Json($$"""{"status":"{{st}}"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await courierToken.PatchAsync($"/api/deliveries/{deliveryId}/status", Json("""{"status":"Failed","comment":"Клиента нет"}"""))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await ivan.PostAsync($"/api/deliveries/{deliveryId}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await courierToken.PostAsync($"/api/deliveries/{deliveryId}/retry", null)).StatusCode);
        var ok = await op.PostAsync($"/api/deliveries/{deliveryId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Assigned", (await Body(ok))["status"]!.ToString());

        var again = await op.PostAsync($"/api/deliveries/{deliveryId}/retry", null);          // уже не Failed
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("DELIVERY_NOT_FAILED", await Code(again));
    }

    private static string CourierLoginFor(JsonNode couriers, string courierId) =>
        couriers.AsArray().First(c => c!["id"]!.ToString() == courierId)!["name"]!.ToString() switch
        {
            var n when n.StartsWith("Алексей") => "alexey", var n when n.StartsWith("Дмитрий") => "dmitry", _ => "olga"
        };
}
