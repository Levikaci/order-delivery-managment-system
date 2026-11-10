using ApiHost;
using Contracts;
using System.Data.Common;
using Npgsql;
using Xunit;

namespace Integration.Tests;

/// <summary>Отказы БД, повторные операции и параллельные запросы.</summary>
public class ReliabilityTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Store _s;
    private readonly Guid _ivan;
    private readonly Product _pizza;
    private static readonly AddressDto Addr = new("Волгоград", "Мира", "15", "24");

    public ReliabilityTests()
    {
        _s = _db.OpenStore();
        _ivan = _s.Customers().First(c => c.Name.StartsWith("Иван")).Id;
        _pizza = _s.Products().First(p => p.Name.Contains("Пицца"));
    }
    public void Dispose() { _s.Dispose(); _db.Dispose(); }

    private CreateOrderRequest Req(Guid? requestId = null, string? comment = null, bool save = false) =>
        new(_ivan, new() { new(_pizza.Id, 1) }, Addr, null, comment, save, requestId);

    private void Trap(string table, string timing = "BEFORE INSERT") => _db.Sql($@"
CREATE OR REPLACE FUNCTION boom() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$ LANGUAGE plpgsql;
CREATE TRIGGER trap {timing} ON {table} FOR EACH ROW EXECUTE FUNCTION boom();");
    private void Untrap(string table) => _db.Sql($"DROP TRIGGER trap ON {table}");

    private long Rows(string t) => _db.Scalar<long>($"SELECT COUNT(*) FROM {t}");

    // ---- отказ при создании заказа: ничего не остаётся наполовину ----
    [Fact]
    public void CreateOrder_DeliveryInsertFails_RollsBackWholeOrder()
    {
        Trap("deliveries");
        Assert.ThrowsAny<DbException>(() => _s.CreateOrder(Req(save: true)));
        Assert.Equal(0, Rows("orders"));
        Assert.Equal(0, Rows("order_items"));
        Assert.Equal(0, Rows("order_history"));
        Assert.Equal(2, Rows("customer_addresses")); // только два демо-адреса, «сохранённого» нет
        Untrap("deliveries");
        Assert.Null(_s.CreateOrder(Req()).error);    // после устранения сбоя всё работает
    }

    // ---- отказ при назначении: заказ создан, доставка Created, назначить можно повторно ----
    [Fact]
    public void CreateOrder_AutoAssignFails_OrderIsSaved_AndRetryAssignWorks()
    {
        Trap("couriers", "BEFORE UPDATE");
        var (d, assigned, e) = _s.CreateOrder(Req());
        Assert.Null(e);
        Assert.False(assigned);
        Assert.Equal(DeliveryStatus.Created, d!.Status);
        Assert.Equal(OrderStatus.Created, d.OrderStatus);
        Assert.Null(d.CourierId);
        Assert.All(_s.Couriers(), c => Assert.Equal(CourierState.Available, c.State)); // курьер не «залип» в Busy

        Assert.ThrowsAny<DbException>(() => _s.Assign(d.DeliveryId, null));            // ручной вызов видит сбой
        Assert.Equal(DeliveryStatus.Created, _s.Delivery(d.DeliveryId)!.Status);       // и состояние не испорчено
        Untrap("couriers");
        Assert.Null(_s.Assign(d.DeliveryId, null));
        Assert.Equal(DeliveryStatus.Assigned, _s.Delivery(d.DeliveryId)!.Status);
    }

    [Fact]
    public void ChangeStatus_Fails_LeavesStatusHistoryAndCourierUntouched()
    {
        var d = _s.CreateOrder(Req()).d!;
        var cid = d.CourierId!.Value;
        Trap("order_history");
        Assert.ThrowsAny<DbException>(() => _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, new("courier", cid, "K")));
        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal(DeliveryStatus.Assigned, x.Status);
        Assert.Equal(2, x.History.Count);
        Assert.Equal(CourierState.Busy, _s.Couriers().First(c => c.Id == cid).State);
        Untrap("order_history");
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, new("courier", cid, "K"))); // повтор после сбоя
    }

    // ---- идемпотентность ----
    [Fact]
    public void CreateOrder_SameRequestId_ReturnsSameOrder_NoDuplicates()
    {
        var rid = Guid.NewGuid();
        var a = _s.CreateOrder(Req(rid));
        var b = _s.CreateOrder(Req(rid));
        Assert.Null(a.error); Assert.Null(b.error);
        Assert.Equal(a.d!.OrderId, b.d!.OrderId);
        Assert.Equal(a.d.DeliveryId, b.d.DeliveryId);
        Assert.True(b.courierAssigned);
        Assert.Equal(1, Rows("orders"));
        Assert.Equal(1, Rows("deliveries"));
        Assert.Equal(1, Rows("couriers WHERE state='Busy'"));
    }

    [Fact]
    public void CreateOrder_WithoutRequestId_CreatesSeparateOrders()
    {
        _s.CreateOrder(Req()); _s.CreateOrder(Req());
        Assert.Equal(2, Rows("orders"));
    }

    [Fact]
    public async Task ParallelDuplicates_WithSameRequestId_CreateOneOrder()
    {
        var rid = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _s.CreateOrder(Req(rid)))));
        Assert.All(results, t => Assert.Null(t.error));
        Assert.Single(results.Select(t => t.d!.OrderId).Distinct());
        Assert.Equal(1, Rows("orders"));
    }

    [Fact]
    public void RepeatedOperations_AreHarmless()
    {
        var d = _s.CreateOrder(Req()).d!;
        var cid = d.CourierId!.Value;
        Assert.Equal("DELIVERY_ALREADY_ASSIGNED", _s.Assign(d.DeliveryId, null));          // повторное назначение
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, new("courier", cid, "K")));
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, new("courier", cid, "K"))); // тот же переход
        Assert.Equal(3, _s.Delivery(d.DeliveryId)!.History.Count);                          // история не задвоилась
        var (again, e) = _s.CreateDelivery(d.OrderId);
        Assert.Equal("DELIVERY_ALREADY_EXISTS", e);
        Assert.Equal(d.DeliveryId, again!.DeliveryId);
        Assert.Equal(1, Rows("deliveries"));
    }

    // ---- параллельные запросы ----
    [Fact]
    public async Task ParallelOrders_OneFreeCourier_OnlyOneGetsAssigned()
    {
        foreach (var c in _s.Couriers().Skip(1)) Assert.Null(_s.SetCourierState(c.Id, CourierState.Offline, true));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _s.CreateOrder(Req()))));
        Assert.All(results, t => Assert.Null(t.error));
        Assert.Equal(1, results.Count(t => t.courierAssigned));
        Assert.Equal(7, results.Count(t => t.d!.Status == DeliveryStatus.Created));
        Assert.Equal(1, Rows("couriers WHERE state='Busy'"));
    }

    [Fact]
    public async Task ParallelAssign_SameDelivery_OnlyOneWins()
    {
        foreach (var c in _s.Couriers()) _s.SetCourierState(c.Id, CourierState.Offline, true);
        var d = _s.CreateOrder(Req()).d!;
        foreach (var c in _s.Couriers()) _s.SetCourierState(c.Id, CourierState.Available, true);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => _s.Assign(d.DeliveryId, null))));
        Assert.Equal(1, results.Count(r => r is null));
        Assert.All(results.Where(r => r is not null), r => Assert.Equal("DELIVERY_ALREADY_ASSIGNED", r));
        Assert.Equal(1, Rows("couriers WHERE state='Busy'"));
    }

    // ---- изменение заказа ----
    [Fact]
    public void UpdateOrder_WithoutComment_KeepsComment_EmptyCommentClearsIt()
    {
        var d = _s.CreateOrder(Req(comment: "Позвонить за 10 минут")).d!;
        Assert.Null(_s.UpdateOrder(d.OrderId, new(new() { new(_pizza.Id, 3) })));
        Assert.Equal("Позвонить за 10 минут", _s.Order(d.OrderId)!.Comment);
        Assert.Null(_s.UpdateOrder(d.OrderId, new(null, null, null, "")));
        Assert.Null(_s.Order(d.OrderId)!.Comment);
    }
}
