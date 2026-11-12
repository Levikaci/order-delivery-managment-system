using ApiHost;
using Contracts;
using Xunit;

namespace Integration.Tests;

/// <summary>Сценарии из ТЗ на настоящем PostgreSQL (отдельная база на каждый тест).</summary>
public class ScenarioTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Store _s;
    private readonly Guid _ivan, _maria;
    private readonly Product _pizza, _juice;
    private static readonly AddressDto Addr = new("Волгоград", "Мира", "15", "24");

    public ScenarioTests()
    {
        _s = _db.OpenStore();
        _ivan = _s.Customers().First(c => c.Name.StartsWith("Иван")).Id;
        _maria = _s.Customers().First(c => c.Name.StartsWith("Мария")).Id;
        _pizza = _s.Products().First(p => p.Name.Contains("Пицца"));
        _juice = _s.Products().First(p => p.Name.Contains("Сок"));
    }

    public void Dispose() { _s.Dispose(); _db.Dispose(); }

    private CreateOrderRequest Req(Guid? customer = null, int qty = 2) =>
        new(customer ?? _ivan, new() { new(_pizza.Id, qty), new(_juice.Id, 1) }, Addr);

    private Session Op => new("operator", null, "Оператор");
    private Session Client(Guid id) => new("client", id, "Клиент");
    private Session Courier(Guid id) => new("courier", id, "Курьер");

    private Delivery Create(Guid? customer = null)
    {
        var (d, _, e) = _s.CreateOrder(Req(customer));
        Assert.Null(e);
        return d!;
    }

    private void Drain() { foreach (var c in _s.Couriers(true)) Assert.Null(_s.SetCourierState(c.Id, CourierState.Offline, true)); }

    // ---- сценарий 1 ----
    [Fact]
    public void Scenario1_CreateOrder_CreatesDeliveryAndAssignsFreeCourier()
    {
        var (d, assigned, e) = _s.CreateOrder(Req());
        Assert.Null(e);
        Assert.True(assigned);
        Assert.Equal(DeliveryStatus.Assigned, d!.Status);
        Assert.Equal(OrderStatus.Confirmed, d.OrderStatus);
        Assert.NotNull(d.CourierId);
        Assert.Equal(2, d.Items.Count);
        Assert.Equal(_pizza.Price * 2 + _juice.Price, d.Total);
        Assert.Equal("г. Волгоград, ул. Мира, д. 15, кв. 24", d.Address);
        Assert.Equal(CourierState.Busy, _s.Couriers().First(c => c.Id == d.CourierId).State);
    }

    [Fact]
    public void CreateOrder_UnknownCustomer_Fails() =>
        Assert.Equal("CUSTOMER_NOT_FOUND", _s.CreateOrder(Req(Guid.NewGuid())).error);

    [Fact]
    public void CreateOrder_Validation()
    {
        Assert.Equal("EMPTY_ORDER", _s.CreateOrder(new(_ivan, new(), Addr)).error);
        Assert.Equal("INVALID_QUANTITY", _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 0) }, Addr)).error);
        Assert.Equal("PRODUCT_NOT_FOUND", _s.CreateOrder(new(_ivan, new() { new(Guid.NewGuid(), 1) }, Addr)).error);
        Assert.Equal("INVALID_ADDRESS", _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 1) }, new("", "Мира", "1"))).error);
        Assert.Equal("ADDRESS_NOT_FOUND", _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 1) }, null, Guid.NewGuid())).error);
    }

    // ---- сценарий 2 ----
    [Fact]
    public void Scenario2_NoCourier_OrderSavedDeliveryCreated_RetryAssignWorks()
    {
        Drain();
        var (d, assigned, e) = _s.CreateOrder(Req());
        Assert.Null(e);
        Assert.False(assigned);
        Assert.Equal(DeliveryStatus.Created, d!.Status);
        Assert.Null(d.CourierId);
        Assert.NotNull(_s.Order(d.OrderId));

        Assert.Equal("COURIER_NOT_AVAILABLE", _s.Assign(d.DeliveryId, null));

        var cr = _s.Couriers().First();
        Assert.Null(_s.SetCourierState(cr.Id, CourierState.Available, true));
        Assert.Null(_s.Assign(d.DeliveryId, null)); // повторное назначение
        Assert.Equal(DeliveryStatus.Assigned, _s.Delivery(d.DeliveryId)!.Status);
    }

    // ---- сценарий 3 ----
    [Fact]
    public void Scenario3_FullCycle_StatusesHistoryAndTimes()
    {
        var d = Create();
        var cid = d.CourierId!.Value;
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Courier(cid)));
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.InTransit, null, Courier(cid)));
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Delivered, null, Courier(cid)));
        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal(DeliveryStatus.Delivered, x.Status);
        Assert.Equal(OrderStatus.Delivered, x.OrderStatus);
        Assert.Equal(new[] { DeliveryStatus.Created, DeliveryStatus.Assigned, DeliveryStatus.Accepted, DeliveryStatus.InTransit, DeliveryStatus.Delivered }, x.History.Select(h => h.Status));
        Assert.Equal(new[] { OrderStatus.Created, OrderStatus.Confirmed, OrderStatus.Preparing, OrderStatus.InDelivery, OrderStatus.Delivered }, x.OrderHistory.Select(h => h.Status));
        Assert.NotNull(x.StartedAt);
        Assert.NotNull(x.FinishedAt);
        Assert.Equal(CourierState.Available, _s.Couriers().First(c => c.Id == cid).State);
    }

    [Fact]
    public void Status_InvalidTransition_Completed_AndIdempotent()
    {
        var d = Create();
        Assert.Equal("INVALID_STATUS_TRANSITION", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Delivered, null, Op));
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Assigned, null, Op)); // уже применён
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Op));
        Assert.Equal("DELIVERY_ALREADY_COMPLETED", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Op));
        Assert.Equal("DELIVERY_NOT_FOUND", _s.ChangeStatus(Guid.NewGuid(), DeliveryStatus.Cancelled, null, Op));
    }

    [Fact]
    public void Failed_RequiresReason_StoresIt_AndFreesCourier()
    {
        var d = Create(); var cid = d.CourierId!.Value;
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Courier(cid));
        Assert.Equal("FAILURE_REASON_REQUIRED", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Failed, " ", Courier(cid)));
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Failed, "Клиент не отвечает", Courier(cid)));
        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal("Клиент не отвечает", x.FailureReason);
        Assert.NotNull(x.FinishedAt);
        Assert.Equal(OrderStatus.DeliveryFailed, x.OrderStatus);
        Assert.Equal(CourierState.Available, _s.Couriers().First(c => c.Id == cid).State);
    }

    [Fact]
    public void Cancel_SetsOrderCancelled_NotDeliveryFailed_AndFreesCourier()
    {
        var d = Create(); var cid = d.CourierId!.Value;
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Client(_ivan)));
        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal(DeliveryStatus.Cancelled, x.Status);
        Assert.Equal(OrderStatus.Cancelled, x.OrderStatus);
        Assert.Null(x.FailureReason);
        Assert.Equal(CourierState.Available, _s.Couriers().First(c => c.Id == cid).State);
        Assert.Equal(new[] { OrderStatus.Created, OrderStatus.Confirmed, OrderStatus.Cancelled }, x.OrderHistory.Select(h => h.Status));
    }

    [Fact]
    public void FailedDelivery_HistoryKeepsDeliveryFailed_AndNoFurtherTransitions()
    {
        var d = Create(); var cid = d.CourierId!.Value;
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Courier(cid));
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.InTransit, null, Courier(cid));
        Assert.Null(_s.ChangeStatus(d.DeliveryId, DeliveryStatus.Failed, "Никого нет дома", Courier(cid)));
        var x = _s.Delivery(d.DeliveryId)!;
        Assert.Equal(OrderStatus.DeliveryFailed, x.OrderStatus);
        Assert.Equal(OrderStatus.DeliveryFailed, x.OrderHistory.Last().Status);
        Assert.Equal("DELIVERY_ALREADY_COMPLETED", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Delivered, null, Op));
        Assert.Equal("DELIVERY_ALREADY_COMPLETED", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Op));
    }

    // ---- роли ----
    [Fact]
    public void Client_CanCancelOnlyOwnOrder_BeforeHandover()
    {
        var d = Create();
        Assert.Equal("UNAUTHORIZED_STATUS_CHANGE", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Client(_maria)));
        Assert.Equal("UNAUTHORIZED_STATUS_CHANGE", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Client(_ivan)));
        var cid = d.CourierId!.Value;
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Courier(cid));
        Assert.Equal("UNAUTHORIZED_STATUS_CHANGE", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Client(_ivan)));

        var d2 = Create();
        Assert.Null(_s.ChangeStatus(d2.DeliveryId, DeliveryStatus.Cancelled, null, Client(_ivan)));
        Assert.Equal(OrderStatus.Cancelled, _s.Delivery(d2.DeliveryId)!.OrderStatus);
    }

    [Fact]
    public void Courier_CannotTouchForeignDelivery_OrCancel()
    {
        var d = Create();
        var other = _s.Couriers().First(c => c.Id != d.CourierId).Id;
        Assert.Equal("UNAUTHORIZED_STATUS_CHANGE", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Courier(other)));
        Assert.Equal("UNAUTHORIZED_STATUS_CHANGE", _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Cancelled, null, Courier(d.CourierId!.Value)));
    }

    [Fact]
    public void Authenticate_ChecksPasswordAndReturnsRole()
    {
        Assert.Equal("operator", _s.Authenticate("operator", "operator")!.Role);
        var c = _s.Authenticate("ivan", "ivan")!;
        Assert.Equal("client", c.Role);
        Assert.Equal(_ivan, c.RefId);
        Assert.Equal("courier", _s.Authenticate("olga", "olga")!.Role);
        Assert.Null(_s.Authenticate("ivan", "wrong"));
        Assert.Null(_s.Authenticate("nobody", "x"));
    }

    // ---- курьеры ----
    [Fact]
    public void Assign_Errors()
    {
        var d = Create();
        Assert.Equal("DELIVERY_ALREADY_ASSIGNED", _s.Assign(d.DeliveryId, null));
        Assert.Equal("DELIVERY_NOT_FOUND", _s.Assign(Guid.NewGuid(), null));
        Drain();
        var d2 = _s.CreateOrder(Req()).d!;
        Assert.Equal("COURIER_NOT_FOUND", _s.Assign(d2.DeliveryId, Guid.NewGuid()));
        Assert.Equal("COURIER_NOT_AVAILABLE", _s.Assign(d2.DeliveryId, _s.Couriers().First().Id));
    }

    [Fact]
    public void CourierStates_Rules()
    {
        var d = Create();
        var busy = d.CourierId!.Value;
        Assert.Equal("COURIER_BUSY", _s.SetCourierState(busy, CourierState.Offline, true));
        var free = _s.Couriers(true).First().Id;
        Assert.Equal("INVALID_COURIER_STATE", _s.SetCourierState(free, CourierState.Busy, true));
        Assert.Equal("FORBIDDEN", _s.SetCourierState(free, CourierState.Blocked, false));
        Assert.Null(_s.SetCourierState(free, CourierState.Blocked, true));
        Assert.DoesNotContain(_s.Couriers(true), c => c.Id == free);
        Assert.Equal("FORBIDDEN", _s.SetCourierState(free, CourierState.Available, false));
        Assert.Null(_s.SetCourierState(free, CourierState.Offline, true));
        Assert.Null(_s.SetCourierState(free, CourierState.Available, false));
    }

    // ---- адреса, заказ, доставка ----
    [Fact]
    public void Addresses_Crud_AndUseInOrder()
    {
        var (a, e) = _s.AddAddress(_ivan, new("Работа", "Волгоград", "Ленина", "7", null));
        Assert.Null(e);
        Assert.Equal(2, _s.Addresses(_ivan).Count);
        Assert.Equal("г. Волгоград, ул. Ленина, д. 7", _s.Address(a!.Id)!.Text);
        var (d, _, e2) = _s.CreateOrder(new(_ivan, new() { new(_pizza.Id, 1) }, null, a.Id));
        Assert.Null(e2);
        Assert.Equal("г. Волгоград, ул. Ленина, д. 7", d!.Address);
        Assert.Equal("ADDRESS_NOT_FOUND", _s.CreateOrder(new(_maria, new() { new(_pizza.Id, 1) }, null, a.Id)).error); // чужой адрес
        Assert.Null(_s.UpdateAddress(a.Id, new("Офис", "Волгоград", "Ленина", "9", "2")));
        Assert.Equal("INVALID_ADDRESS", _s.UpdateAddress(a.Id, new("x", "", "Ленина", "9", null)));
        Assert.Null(_s.DeleteAddress(a.Id));
        Assert.Equal("ADDRESS_NOT_FOUND", _s.DeleteAddress(a.Id));
    }

    [Fact]
    public void SaveAddress_StoresInlineAddress()
    {
        var before = _s.Addresses(_maria).Count;
        _s.CreateOrder(new(_maria, new() { new(_pizza.Id, 1) }, new("Волгоград", "Рокоссовского", "21", null), null, null, true));
        Assert.Equal(before + 1, _s.Addresses(_maria).Count);
    }

    [Fact]
    public void UpdateOrder_AllowedBeforeAccept_ThenLocked()
    {
        var d = Create();
        Assert.Null(_s.UpdateOrder(d.OrderId, new(new() { new(_juice.Id, 5) }, new("Волгоград", "Ленина", "1", null), null, "Позвонить")));
        var x = _s.Order(d.OrderId)!;
        Assert.Single(x.Items);
        Assert.Equal(5, x.Items[0].Quantity);
        Assert.Equal("Позвонить", x.Comment);
        Assert.Contains("Ленина", x.Address);
        Assert.Equal("EMPTY_ORDER", _s.UpdateOrder(d.OrderId, new(new(), null)));
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Courier(d.CourierId!.Value));
        Assert.Equal("ORDER_NOT_EDITABLE", _s.UpdateOrder(d.OrderId, new(new() { new(_juice.Id, 1) }, null)));
        Assert.Equal("ORDER_NOT_FOUND", _s.UpdateOrder(Guid.NewGuid(), new(null, null)));
    }

    [Fact]
    public void CreateDelivery_IsIdempotentByOrderId()
    {
        var d = Create();
        var (again, e) = _s.CreateDelivery(d.OrderId);
        Assert.Equal("DELIVERY_ALREADY_EXISTS", e);
        Assert.Equal(d.DeliveryId, again!.DeliveryId);
        Assert.Equal("ORDER_NOT_FOUND", _s.CreateDelivery(Guid.NewGuid()).error);
    }

    [Fact]
    public void ConfirmReceipt_OnlyAfterDelivered()
    {
        var d = Create(); var cid = d.CourierId!.Value;
        Assert.Equal("INVALID_STATUS_TRANSITION", _s.ConfirmReceipt(d.DeliveryId, Client(_ivan)));
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Accepted, null, Courier(cid));
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.InTransit, null, Courier(cid));
        _s.ChangeStatus(d.DeliveryId, DeliveryStatus.Delivered, null, Courier(cid));
        Assert.Equal("FORBIDDEN", _s.ConfirmReceipt(d.DeliveryId, Client(_maria)));
        Assert.Null(_s.ConfirmReceipt(d.DeliveryId, Client(_ivan)));
        Assert.NotNull(_s.Delivery(d.DeliveryId)!.ConfirmedAt);
    }

    [Fact]
    public void Search_FindsByItemAndAddress()
    {
        Create();
        Assert.Single(_s.Deliveries("Пицца"));
        Assert.Single(_s.Deliveries("Мира"));
        Assert.Empty(_s.Deliveries("несуществующее"));
    }

    [Fact]
    public void Snapshot_IsFilteredByRole()
    {
        Create(_ivan); Create(_maria);
        int N(Session me) => System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(_s.Snapshot(me)))
            .RootElement.GetProperty("deliveries").GetArrayLength();
        Assert.Equal(1, N(Client(_ivan)));
        Assert.Equal(2, N(Op));
    }

    [Fact]
    public void Directories_DeleteBlockedWhenOrdersExist_AndCredentialsCreated()
    {
        var d = Create();
        Assert.Equal("CUSTOMER_HAS_ORDERS", _s.DeleteCustomer(_ivan));
        Assert.Equal("COURIER_HAS_ORDERS", _s.DeleteCourier(d.CourierId!.Value));
        var (created, e) = _s.NewCustomer(new("Новый Клиент", "+7 000", "newcli", "pw1-secret"));
        Assert.Null(e);
        Assert.NotNull(created);
        Assert.Equal("client", _s.Authenticate("newcli", "pw1-secret")!.Role);
        Assert.Equal("LOGIN_TAKEN", _s.NewCustomer(new("Другой", null, "newcli", "x-secret-2")).error);
        Assert.Equal("INVALID_NAME", _s.NewCourier(new(" ", null)).error);
    }
}
