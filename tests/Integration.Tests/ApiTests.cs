using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Integration.Tests;

/// <summary>Приложение целиком (HTTP → роли → Store → PostgreSQL) на отдельной базе.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public TestDb Db { get; }
    private readonly bool _ownsDb;
    private readonly IReadOnlyDictionary<string, string> _settings;
    public ApiFactory(TestDb? db = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        _ownsDb = db is null; Db = db ?? new TestDb(); _settings = settings ?? new Dictionary<string, string>();
    }
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder b)
    {
        b.UseSetting("ConnectionStrings:Default", Db.ConnectionString).UseSetting("Logging:LogLevel:Default", "Warning")
         .UseSetting("Assignment:Enabled", "false"); // фоновое назначение проверяется отдельными тестами, здесь оно мешало бы сценариям
        foreach (var (k, v) in _settings) b.UseSetting(k, v);
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (_ownsDb && disposing) Db.Dispose(); }
}

public class ApiTests : IDisposable
{
    protected readonly ApiFactory F = new();
    public void Dispose() => F.Dispose();

    protected static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    protected async Task<HttpClient> LoginAsync(string login, string? password = null, ApiFactory? f = null)
    {
        var c = (f ?? F).CreateClient();
        var r = await c.PostAsync("/api/auth/login", Json($$"""{"login":"{{login}}","password":"{{password ?? login}}"}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var token = (await r.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.ToString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    protected static string OrderJson(string customerId, string productId, int qty = 1) =>
        "{\"customerId\":\"" + customerId + "\",\"items\":[{\"productId\":\"" + productId + "\",\"quantity\":" + qty +
        "}],\"deliveryAddress\":{\"city\":\"В\",\"street\":\"М\",\"house\":\"1\"}}";

    protected static async Task<JsonNode> Body(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonNode>())!;
    protected static async Task<string?> Code(HttpResponseMessage r) => (await Body(r))["errorCode"]?.ToString();

    protected async Task<(string pizza, string juice, string customerId)> DemoIds(HttpClient op)
    {
        var products = (await Body(await op.GetAsync("/api/products"))).AsArray();
        var customers = (await Body(await op.GetAsync("/api/customers"))).AsArray();
        return (products.First(p => p!["name"]!.ToString().Contains("Пицца"))!["id"]!.ToString(),
                products.First(p => p!["name"]!.ToString().Contains("Сок"))!["id"]!.ToString(),
                customers.First(c => c!["name"]!.ToString().StartsWith("Иван"))!["id"]!.ToString());
    }

    protected async Task<JsonNode> CreateOrderAs(HttpClient client, string customerId, string productId, int qty = 1, string? requestId = null)
    {
        var rid = requestId is null ? "" : $$""","requestId":"{{requestId}}" """;
        var r = await client.PostAsync("/api/orders", Json($$"""{"customerId":"{{customerId}}","items":[{"productId":"{{productId}}","quantity":{{qty}}}],"deliveryAddress":{"city":"Волгоград","street":"Мира","house":"15"}{{rid}}}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Body(r);
    }
}

public class ApiRoleTests : ApiTests
{
    public static IEnumerable<object[]> Endpoints() => new[]
    {
        // метод, путь, роли, которым ДОСТУП ЗАПРЕЩЁН
        new object[] { "GET", "/api/customers", "client,courier" },
        new object[] { "POST", "/api/customers", "client,courier" },
        new object[] { "GET", "/api/couriers", "client,courier" },
        new object[] { "GET", "/api/couriers/available", "client,courier" },
        new object[] { "POST", "/api/couriers", "client,courier" },
        new object[] { "POST", "/api/products", "client,courier" },
        new object[] { "DELETE", "/api/products/{id}", "client,courier" },
        new object[] { "POST", "/api/orders", "operator,courier" },
        new object[] { "POST", "/api/deliveries", "client,courier" },
        new object[] { "POST", "/api/deliveries/{id}/assign", "client,courier" },
        new object[] { "POST", "/api/deliveries/{id}/confirm", "courier" },
        new object[] { "PATCH", "/api/couriers/{id}/state", "client" },
        new object[] { "GET", "/api/customers/{id}/addresses", "courier" },
        new object[] { "PUT", "/api/orders/{id}", "courier" },
    };

    private static HttpRequestMessage Req(string method, string path)
    {
        var m = new HttpRequestMessage(new HttpMethod(method), path.Replace("{id}", Guid.NewGuid().ToString()));
        if (method is "POST" or "PUT" or "PATCH") m.Content = Json("{}");
        return m;
    }

    [Theory, MemberData(nameof(Endpoints))]
    public async Task WithoutToken_Is401(string method, string path, string _)
    {
        var r = await F.CreateClient().SendAsync(Req(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("UNAUTHORIZED", await Code(r));
    }

    [Theory, MemberData(nameof(Endpoints))]
    public async Task ForbiddenRoles_Get403_InCommonErrorFormat(string method, string path, string forbidden)
    {
        foreach (var login in forbidden.Split(','))
        {
            using var c = await LoginAsync(login switch { "client" => "ivan", "courier" => "alexey", _ => "operator" });
            var r = await c.SendAsync(Req(method, path));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            var b = await Body(r);
            Assert.False((bool)b["success"]!);
            Assert.Equal("FORBIDDEN", b["errorCode"]!.ToString());
            Assert.False(string.IsNullOrWhiteSpace(b["message"]!.ToString()));
        }
    }

    [Fact]
    public async Task Client_SeesOnlyOwnData()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        using var maria = await LoginAsync("maria");
        var order = await CreateOrderAs(ivan, ivanId, pizza);

        var mine = (await Body(await maria.GetAsync("/api/deliveries"))).AsArray();
        Assert.Empty(mine);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"/api/orders/{order["orderId"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"/api/deliveries/{order["deliveryId"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"/api/orders/{order["orderId"]}/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"/api/customers/{ivanId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.GetAsync($"/api/customers/{ivanId}/addresses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.PutAsync($"/api/orders/{order["orderId"]}", Json("""{"comment":"взлом"}"""))).StatusCode);
        Assert.Single((await Body(await ivan.GetAsync("/api/deliveries"))).AsArray());
        Assert.Single((await Body(await maria.GetAsync("/api/state")))["customers"]!.AsArray());
    }

    [Fact]
    public async Task Client_CannotOrderForSomeoneElse_AndOperatorCannotCreateOrders()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var maria = await LoginAsync("maria");
        var body = Json(OrderJson(ivanId, pizza));
        Assert.Equal(HttpStatusCode.Forbidden, (await maria.PostAsync("/api/orders", body)).StatusCode);
        var body2 = Json(OrderJson(ivanId, pizza));
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync("/api/orders", body2)).StatusCode);
    }

    [Fact]
    public async Task StatusRights_ByRole_OverHttp()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        using var maria = await LoginAsync("maria");
        var order = await CreateOrderAs(ivan, ivanId, pizza);
        var did = order["deliveryId"]!.ToString();
        var courierLogin = (await Body(await op.GetAsync("/api/couriers"))).AsArray().First(c => c!["id"]!.ToString() == order["courierId"]!.ToString())!["name"]!.ToString().Split(' ')[0] switch
        { "Алексей" => "alexey", "Дмитрий" => "dmitry", _ => "olga" };
        var otherCourier = courierLogin == "alexey" ? "dmitry" : "alexey";
        using var mine = await LoginAsync(courierLogin);
        using var other = await LoginAsync(otherCourier);

        async Task<HttpResponseMessage> Patch(HttpClient c, string status, string? comment = null) =>
            await c.PatchAsync($"/api/deliveries/{did}/status", Json($$"""{"status":"{{status}}","comment":{{(comment is null ? "null" : $"\"{comment}\"")}}}"""));

        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(maria, "Cancelled")).StatusCode);       // чужой клиент
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(ivan, "Accepted")).StatusCode);          // клиент только отменяет
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(other, "Accepted")).StatusCode);         // чужой курьер
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(mine, "Cancelled")).StatusCode);         // курьер не отменяет
        Assert.Equal(HttpStatusCode.OK, (await Patch(mine, "Accepted")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(ivan, "Cancelled")).StatusCode);         // после принятия клиент не отменит
        Assert.Equal(HttpStatusCode.Conflict, (await Patch(mine, "Delivered")).StatusCode);          // Accepted → Delivered недопустим
        Assert.Equal("INVALID_STATUS_TRANSITION", await Code(await Patch(mine, "Delivered")));
    }

    [Fact]
    public async Task CourierState_CourierChangesOnlyOwn()
    {
        using var op = await LoginAsync("operator");
        var couriers = (await Body(await op.GetAsync("/api/couriers"))).AsArray();
        var alexey = couriers.First(c => c!["name"]!.ToString().StartsWith("Алексей"))!["id"]!.ToString();
        var dmitry = couriers.First(c => c!["name"]!.ToString().StartsWith("Дмитрий"))!["id"]!.ToString();
        using var a = await LoginAsync("alexey");
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PatchAsync($"/api/couriers/{dmitry}/state", Json("""{"state":"Offline"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsync($"/api/couriers/{alexey}/state", Json("""{"state":"Offline"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PatchAsync($"/api/couriers/{alexey}/state", Json("""{"state":"Blocked"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PatchAsync($"/api/couriers/{alexey}/state", Json("""{"state":"Blocked"}"""))).StatusCode);
    }

    [Fact]
    public async Task Logout_InvalidatesToken()
    {
        using var c = await LoginAsync("ivan");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
    }
}

public class ApiValidationTests : ApiTests
{
    [Theory]
    [InlineData("ivan", "wrong")]
    [InlineData("nobody", "x")]
    [InlineData("", "")]
    public async Task Login_BadCredentials_401(string login, string password)
    {
        var r = await F.CreateClient().PostAsync("/api/auth/login", Json($$"""{"login":"{{login}}","password":"{{password}}"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await Code(r));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public async Task MalformedJson_Is400_InvalidRequest(string body)
    {
        using var ivan = await LoginAsync("ivan");
        var r = await ivan.PostAsync("/api/orders", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("INVALID_REQUEST", await Code(r));
    }

    [Fact]
    public async Task EmptyBody_Is400()
    {
        using var ivan = await LoginAsync("ivan");
        var r = await ivan.PostAsync("/api/orders", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task OrderValidation_ReturnsSpecificCodes()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        async Task<(HttpStatusCode, string?)> Post(string items, string address = """{"city":"В","street":"М","house":"1"}""")
        {
            var r = await ivan.PostAsync("/api/orders", Json($$"""{"customerId":"{{ivanId}}","items":{{items}},"deliveryAddress":{{address}}}"""));
            return (r.StatusCode, r.IsSuccessStatusCode ? null : await Code(r));
        }
        Assert.Equal((HttpStatusCode.BadRequest, "EMPTY_ORDER"), await Post("[]"));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_QUANTITY"), await Post($$"""[{"productId":"{{pizza}}","quantity":0}]"""));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_QUANTITY"), await Post($$"""[{"productId":"{{pizza}}","quantity":100}]"""));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_QUANTITY"), await Post($$"""[{"productId":"{{pizza}}","quantity":-5}]"""));
        Assert.Equal((HttpStatusCode.NotFound, "PRODUCT_NOT_FOUND"), await Post($$"""[{"productId":"{{Guid.NewGuid()}}","quantity":1}]"""));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_ADDRESS"), await Post($$"""[{"productId":"{{pizza}}","quantity":1}]""", """{"city":"","street":"М","house":"1"}"""));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_ADDRESS"), await Post($$"""[{"productId":"{{pizza}}","quantity":1}]""", "null"));
    }

    [Theory]
    [InlineData("""{"name":"","price":10}""")]
    [InlineData("""{"name":"   ","price":10}""")]
    [InlineData("""{"name":"Чай","price":-1}""")]
    [InlineData("""{"name":"Чай","price":1000000.01}""")]
    public async Task ProductValidation_400(string body)
    {
        using var op = await LoginAsync("operator");
        var r = await op.PostAsync("/api/products", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("INVALID_PRODUCT", await Code(r));
    }

    [Fact]
    public async Task UnknownEnumValue_AndBadIds_AreClientErrors()
    {
        using var op = await LoginAsync("operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PatchAsync($"/api/deliveries/{Guid.NewGuid()}/status", Json("""{"status":"Flying"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/api/orders/not-a-guid")).StatusCode);
        var r = await op.GetAsync($"/api/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("ORDER_NOT_FOUND", await Code(r));
        Assert.Equal("DELIVERY_NOT_FOUND", await Code(await op.PostAsync($"/api/deliveries/{Guid.NewGuid()}/assign", Json("{}"))));
    }

    [Fact]
    public async Task FailedWithoutReason_Is400()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        var o = await CreateOrderAs(ivan, ivanId, pizza);
        var did = o["deliveryId"]!.ToString();
        await op.PatchAsync($"/api/deliveries/{did}/status", Json("""{"status":"Accepted"}"""));
        var r = await op.PatchAsync($"/api/deliveries/{did}/status", Json("""{"status":"Failed","comment":"  "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("FAILURE_REASON_REQUIRED", await Code(r));
    }

    [Fact]
    public async Task NoFreeCourier_Assign_Is409()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        foreach (var c in (await Body(await op.GetAsync("/api/couriers"))).AsArray())
            await op.PatchAsync($"/api/couriers/{c!["id"]}/state", Json("""{"state":"Offline"}"""));
        using var ivan = await LoginAsync("ivan");
        var o = await CreateOrderAs(ivan, ivanId, pizza);
        Assert.False((bool)o["courierAssigned"]!);
        Assert.Equal("Created", o["status"]!.ToString());
        var r = await op.PostAsync($"/api/deliveries/{o["deliveryId"]}/assign", Json("{}"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("COURIER_NOT_AVAILABLE", await Code(r));
    }
}

public class ApiScenarioTests : ApiTests
{
    [Fact]
    public async Task FullLifecycle_OverHttp_Delivered()
    {
        using var op = await LoginAsync("operator");
        var (pizza, juice, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        var o = await CreateOrderAs(ivan, ivanId, pizza, qty: 2);
        Assert.True((bool)o["courierAssigned"]!);
        Assert.Equal("Assigned", o["status"]!.ToString());
        Assert.Equal("Confirmed", o["orderStatus"]!.ToString());
        var did = o["deliveryId"]!.ToString();
        var cid = o["courierId"]!.ToString();
        var name = (await Body(await op.GetAsync("/api/couriers"))).AsArray().First(c => c!["id"]!.ToString() == cid)!["name"]!.ToString();
        using var courier = await LoginAsync(name.StartsWith("Алексей") ? "alexey" : name.StartsWith("Дмитрий") ? "dmitry" : "olga");

        foreach (var st in new[] { "Accepted", "InTransit", "Delivered" })
            Assert.Equal(HttpStatusCode.OK, (await courier.PatchAsync($"/api/deliveries/{did}/status", Json($$"""{"status":"{{st}}"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ivan.PostAsync($"/api/deliveries/{did}/confirm", null)).StatusCode);

        var d = await Body(await ivan.GetAsync($"/api/deliveries/{did}"));
        Assert.Equal("Delivered", d["status"]!.ToString());
        Assert.Equal("Delivered", d["orderStatus"]!.ToString());
        Assert.NotNull(d["confirmedAt"]);
        Assert.Equal(5, d["history"]!.AsArray().Count);
        Assert.Equal(1180m, (decimal)d["total"]!);
        Assert.NotNull(juice);
    }

    [Fact]
    public async Task FailedDelivery_OverHttp_OrderIsDeliveryFailed_NotCancelled()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        var o = await CreateOrderAs(ivan, ivanId, pizza);
        var did = o["deliveryId"]!.ToString();
        await op.PatchAsync($"/api/deliveries/{did}/status", Json("""{"status":"Accepted"}"""));
        Assert.Equal(HttpStatusCode.OK, (await op.PatchAsync($"/api/deliveries/{did}/status", Json("""{"status":"Failed","comment":"Адрес не найден"}"""))).StatusCode);
        var d = await Body(await op.GetAsync($"/api/deliveries/{did}"));
        Assert.Equal("Failed", d["status"]!.ToString());
        Assert.Equal("DeliveryFailed", d["orderStatus"]!.ToString());
        Assert.Equal("Адрес не найден", d["failureReason"]!.ToString());
    }

    [Fact]
    public async Task Cancel_OverHttp_OrderIsCancelled()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        var o = await CreateOrderAs(ivan, ivanId, pizza);
        var did = o["deliveryId"]!.ToString();
        Assert.Equal(HttpStatusCode.OK, (await ivan.PatchAsync($"/api/deliveries/{did}/status", Json("""{"status":"Cancelled"}"""))).StatusCode);
        var d = await Body(await ivan.GetAsync($"/api/deliveries/{did}"));
        Assert.Equal(("Cancelled", "Cancelled"), (d["status"]!.ToString(), d["orderStatus"]!.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await ivan.PatchAsync($"/api/deliveries/{did}/status", Json("""{"status":"Accepted"}"""))).StatusCode);
        var again = await op.PatchAsync($"/api/deliveries/{did}/status", Json("""{"status":"Accepted"}"""));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("DELIVERY_ALREADY_COMPLETED", await Code(again));
    }

    [Fact]
    public async Task PostOrders_WithSameRequestId_IsIdempotent()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        var rid = Guid.NewGuid().ToString();
        var a = await CreateOrderAs(ivan, ivanId, pizza, requestId: rid);
        var b = await CreateOrderAs(ivan, ivanId, pizza, requestId: rid);
        Assert.Equal(a["orderId"]!.ToString(), b["orderId"]!.ToString());
        Assert.Single((await Body(await ivan.GetAsync("/api/deliveries"))).AsArray());
    }

    [Fact]
    public async Task OrderEdit_IsVisibleInChangesEndpoint()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        var o = await CreateOrderAs(ivan, ivanId, pizza);
        Assert.Equal(HttpStatusCode.OK, (await ivan.PutAsync($"/api/orders/{o["orderId"]}", Json("""{"comment":"домофон 24"}"""))).StatusCode);
        var changes = (await Body(await ivan.GetAsync($"/api/orders/{o["orderId"]}/changes"))).AsArray();
        var ch = Assert.Single(changes)!;
        Assert.Equal("Comment", ch["field"]!.ToString());
        Assert.Equal("домофон 24", ch["newValue"]!.ToString());
        Assert.Equal("Иван Петров", ch["actorName"]!.ToString());
        Assert.NotNull(ch["actorId"]);
    }

    [Fact]
    public async Task Money_RoundTripsExactly_OverHttp()
    {
        using var op = await LoginAsync("operator");
        var created = await Body(await op.PostAsync("/api/products", Json("""{"name":"Чай","price":19.99}""")));
        var pid = created["id"]!.ToString();
        Assert.Equal(19.99m, (decimal)created["price"]!);
        var (_, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        var o = await CreateOrderAs(ivan, ivanId, pid, qty: 3);
        var d = await Body(await ivan.GetAsync($"/api/orders/{o["orderId"]}"));
        Assert.Equal(59.97m, (decimal)d["total"]!);
        Assert.Equal(HttpStatusCode.OK, (await op.PutAsync($"/api/products/{pid}", Json("""{"name":"Чай","price":0.005}"""))).StatusCode);
        var price = (await Body(await op.GetAsync("/api/products"))).AsArray().First(p => p!["id"]!.ToString() == pid)!["price"]!;
        Assert.Equal(0.01m, (decimal)price);                                  // округление до копейки
        Assert.Equal(59.97m, (decimal)(await Body(await ivan.GetAsync($"/api/orders/{o["orderId"]}")))["total"]!); // цена в заказе зафиксирована
    }

    [Fact]
    public async Task DataSurvivesApplicationRestart()
    {
        string orderId;
        using (var op = await LoginAsync("operator"))
        {
            var (pizza, _, ivanId) = await DemoIds(op);
            using var ivan = await LoginAsync("ivan");
            orderId = (await CreateOrderAs(ivan, ivanId, pizza))["orderId"]!.ToString();
        }
        using var second = new ApiFactory(F.Db);                               // новый экземпляр приложения на той же базе
        using var again = await LoginAsync("ivan", f: second);                  // сессии в памяти — вход заново
        var d = await Body(await again.GetAsync($"/api/orders/{orderId}"));
        Assert.Equal("Confirmed", d["orderStatus"]!.ToString());
        Assert.Equal(8, (await Body(await again.GetAsync("/api/state")))["products"]!.AsArray().Count);   // демо-данные не засеяны повторно
    }

    [Fact]
    public async Task DatabaseFailure_Returns500_InCommonFormat_AndLeavesNoPartialOrder()
    {
        using var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        using var ivan = await LoginAsync("ivan");
        F.Db.Sql(@"CREATE FUNCTION boom() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$ LANGUAGE plpgsql;
                   CREATE TRIGGER trap BEFORE INSERT ON deliveries FOR EACH ROW EXECUTE FUNCTION boom();");
        var r = await ivan.PostAsync("/api/orders", Json(OrderJson(ivanId, pizza)));
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var b = await Body(r);
        Assert.Equal("STORAGE_ERROR", b["errorCode"]!.ToString());
        Assert.DoesNotContain("injected", b.ToJsonString());                    // детали сбоя не утекают клиенту
        Assert.Equal(0, F.Db.Scalar<long>("SELECT COUNT(*) FROM orders"));
        F.Db.Sql("DROP TRIGGER trap ON deliveries");
        Assert.Equal(HttpStatusCode.OK, (await ivan.PostAsync("/api/orders", Json(OrderJson(ivanId, pizza)))).StatusCode);
    }
}
