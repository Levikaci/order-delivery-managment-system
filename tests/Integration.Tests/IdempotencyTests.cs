using System.Net;
using ApiHost;
using Contracts;
using Xunit;

namespace Integration.Tests;

/// <summary>Идемпотентность POST /api/orders: повтор requestId с теми же данными и с другими.</summary>
public class IdempotencyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Store _s;
    private readonly Guid _ivan, _maria;
    private readonly Product _pizza, _juice;
    private static readonly AddressDto Addr = new("Волгоград", "Мира", "15", "24");

    public IdempotencyTests()
    {
        _s = _db.OpenStore();
        _ivan = _s.Customers().First(c => c.Name.StartsWith("Иван")).Id;
        _maria = _s.Customers().First(c => c.Name.StartsWith("Мария")).Id;
        _pizza = _s.Products().First(p => p.Name.Contains("Пицца"));
        _juice = _s.Products().First(p => p.Name.Contains("Сок"));
    }
    public void Dispose() { _s.Dispose(); _db.Dispose(); }

    private CreateOrderRequest Req(Guid rid, Guid? customer = null, List<ItemRequest>? items = null, AddressDto? addr = null, string? comment = "Позвонить", Guid? addressId = null, bool save = false) =>
        new(customer ?? _ivan, items ?? new() { new(_pizza.Id, 2), new(_juice.Id, 1) }, addressId is null ? addr ?? Addr : null, addressId, comment, save, rid);

    private long Orders => _db.Scalar<long>("SELECT COUNT(*) FROM orders");

    [Fact]
    public void IdenticalRepeat_ReturnsSameOrder()
    {
        var rid = Guid.NewGuid();
        var a = _s.CreateOrder(Req(rid));
        var b = _s.CreateOrder(Req(rid));
        Assert.Null(b.error);
        Assert.Equal(a.d!.OrderId, b.d!.OrderId);
        Assert.Equal(1, Orders);
    }

    [Fact]
    public void Repeat_WithEquivalentData_IsTheSameRequest()
    {
        var rid = Guid.NewGuid();
        var a = _s.CreateOrder(Req(rid, comment: "Позвонить"));
        // другой порядок позиций, тот же товар двумя строками, пробелы по краям — те же данные заказа
        var same = Req(rid, items: new() { new(_juice.Id, 1), new(_pizza.Id, 1), new(_pizza.Id, 1) },
            addr: new(" Волгоград ", "Мира ", " 15", "24 "), comment: "  Позвонить ");
        var b = _s.CreateOrder(same);
        Assert.Null(b.error);
        Assert.Equal(a.d!.OrderId, b.d!.OrderId);
        Assert.Equal(1, Orders);
    }

    public static TheoryData<string> Changes => new() { "items", "quantity", "address", "apartment", "comment", "no-comment", "saved-address", "save-flag" };

    [Theory]
    [MemberData(nameof(Changes))]
    public void Repeat_WithChangedData_IsConflict(string change)
    {
        var rid = Guid.NewGuid();
        var first = _s.CreateOrder(Req(rid));
        Assert.Null(first.error);
        var savedAddress = _s.Addresses(_ivan).First().Id;
        var changed = change switch
        {
            "items" => Req(rid, items: new() { new(_pizza.Id, 2) }),
            "quantity" => Req(rid, items: new() { new(_pizza.Id, 3), new(_juice.Id, 1) }),
            "address" => Req(rid, addr: new("Волгоград", "Ленина", "15", "24")),
            "apartment" => Req(rid, addr: new("Волгоград", "Мира", "15", "25")),
            "comment" => Req(rid, comment: "Не звонить"),
            "no-comment" => Req(rid, comment: null),
            "saved-address" => Req(rid, addressId: savedAddress),
            "save-flag" => Req(rid, save: true),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var (d, assigned, e) = _s.CreateOrder(changed);
        Assert.Equal("IDEMPOTENCY_CONFLICT", e);
        Assert.Null(d);
        Assert.False(assigned);
        Assert.Equal(1, Orders);
        Assert.Equal(2, _s.Order(first.d!.OrderId)!.Items.Count);                 // исходный заказ не изменился
    }

    [Fact]
    public void SameRequestId_ForDifferentCustomers_CreatesTwoOrders()
    {
        var rid = Guid.NewGuid();
        var a = _s.CreateOrder(Req(rid, _ivan));
        var b = _s.CreateOrder(Req(rid, _maria));
        Assert.Null(a.error); Assert.Null(b.error);
        Assert.NotEqual(a.d!.OrderId, b.d!.OrderId);
        Assert.Equal(_maria, b.d.CustomerId);
        Assert.Equal(2, Orders);
    }

    [Fact]
    public void WithoutRequestId_EveryRequestCreatesOrder()
    {
        var r = new CreateOrderRequest(_ivan, new() { new(_pizza.Id, 1) }, Addr);
        _s.CreateOrder(r); _s.CreateOrder(r);
        Assert.Equal(2, Orders);
        Assert.Equal(0, _db.Scalar<long>("SELECT COUNT(*) FROM orders WHERE request_hash IS NOT NULL"));
    }

    [Fact]
    public void OrderCreatedBeforeFingerprints_IsReturnedOnRepeat()
    {
        var rid = Guid.NewGuid();
        var a = _s.CreateOrder(Req(rid));
        _db.Sql("UPDATE orders SET request_hash = NULL");                         // заказ из версии до миграции 0005
        var b = _s.CreateOrder(Req(rid, comment: "другой"));
        Assert.Null(b.error);
        Assert.Equal(a.d!.OrderId, b.d!.OrderId);
    }

    [Fact]
    public void InvalidFirstRequest_DoesNotReserveRequestId()
    {
        var rid = Guid.NewGuid();
        Assert.Equal("EMPTY_ORDER", _s.CreateOrder(Req(rid, items: new())).error);
        Assert.Null(_s.CreateOrder(Req(rid)).error);                               // исправленный запрос с тем же ключом проходит
        Assert.Equal(1, Orders);
    }

    [Fact]
    public async Task ParallelIdenticalRequests_CreateOneOrder()
    {
        var rid = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _s.CreateOrder(Req(rid)))));
        Assert.All(results, r => Assert.Null(r.error));
        Assert.Single(results.Select(r => r.d!.OrderId).Distinct());
        Assert.Equal(1, Orders);
        Assert.Equal(1, _db.Scalar<long>("SELECT COUNT(*) FROM deliveries"));
    }

    [Fact]
    public async Task ParallelRequests_SameRequestIdDifferentData_OnlyOneWins()
    {
        var rid = Guid.NewGuid();
        var variants = Enumerable.Range(1, 8).Select(q => Req(rid, items: new() { new(_pizza.Id, q) })).ToList();
        var results = await Task.WhenAll(variants.Select(v => Task.Run(() => _s.CreateOrder(v))));
        Assert.Single(results, r => r.error is null);
        Assert.All(results.Where(r => r.error is not null), r => Assert.Equal("IDEMPOTENCY_CONFLICT", r.error));
        Assert.Equal(1, Orders);
        var winner = results.Single(r => r.error is null).d!;
        var saved = _s.Order(winner.OrderId)!;
        Assert.Equal(winner.Items.Single().Quantity, saved.Items.Single().Quantity); // сохранён именно выигравший запрос
    }

    [Fact]
    public void Fingerprint_DoesNotDependOnRequestId() =>
        Assert.Equal(Req(Guid.NewGuid()).Fingerprint(), Req(Guid.NewGuid()).Fingerprint());
}

public class IdempotencyApiTests : ApiTests
{
    [Fact]
    public async Task RepeatWithOtherItems_Returns409_IdempotencyConflict()
    {
        var op = await LoginAsync("operator");
        var (pizza, juice, ivanId) = await DemoIds(op);
        var ivan = await LoginAsync("ivan");
        var rid = Guid.NewGuid().ToString();
        var first = await CreateOrderAs(ivan, ivanId, pizza, 1, rid);
        var again = await CreateOrderAs(ivan, ivanId, pizza, 1, rid);
        Assert.Equal(first["orderId"]!.ToString(), again["orderId"]!.ToString());

        var other = await ivan.PostAsync("/api/orders", Json($$"""{"customerId":"{{ivanId}}","items":[{"productId":"{{juice}}","quantity":1}],"deliveryAddress":{"city":"Волгоград","street":"Мира","house":"15"},"requestId":"{{rid}}"}"""));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        var body = await Body(other);
        Assert.Equal("IDEMPOTENCY_CONFLICT", body["errorCode"]!.ToString());
        Assert.False(body["success"]!.GetValue<bool>());
        Assert.Equal(1, F.Db.Scalar<long>("SELECT COUNT(*) FROM orders"));
    }

    [Fact]
    public async Task SameRequestId_FromAnotherClient_IsIndependent()
    {
        var op = await LoginAsync("operator");
        var (pizza, _, ivanId) = await DemoIds(op);
        var mariaId = (await Body(await op.GetAsync("/api/customers"))).AsArray().First(c => c!["name"]!.ToString().StartsWith("Мария"))!["id"]!.ToString();
        var rid = Guid.NewGuid().ToString();
        var a = await CreateOrderAs(await LoginAsync("ivan"), ivanId, pizza, 1, rid);
        var b = await CreateOrderAs(await LoginAsync("maria"), mariaId, pizza, 1, rid);
        Assert.NotEqual(a["orderId"]!.ToString(), b["orderId"]!.ToString());
    }
}
