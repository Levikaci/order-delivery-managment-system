using Contracts;
using CourierModule;
using CustomerModule;
using DeliveryModule;
using OrderModule;

namespace OrderModule.Tests;

public class RulesTests
{
    [Theory]
    [InlineData(DeliveryStatus.Created, DeliveryStatus.Assigned, true)]
    [InlineData(DeliveryStatus.Assigned, DeliveryStatus.Accepted, true)]
    [InlineData(DeliveryStatus.Accepted, DeliveryStatus.InTransit, true)]
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.Delivered, true)]
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.Failed, true)]
    [InlineData(DeliveryStatus.Created, DeliveryStatus.Delivered, false)]
    [InlineData(DeliveryStatus.Accepted, DeliveryStatus.Cancelled, false)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Created, false)]
    public void DeliveryTransitions(DeliveryStatus from, DeliveryStatus to, bool ok) =>
        Assert.Equal(ok, DeliveryRules.CanTransition(from, to));

    [Theory]
    [InlineData(DeliveryStatus.Created, OrderStatus.Created)]
    [InlineData(DeliveryStatus.Assigned, OrderStatus.Confirmed)]
    [InlineData(DeliveryStatus.Accepted, OrderStatus.Preparing)]
    [InlineData(DeliveryStatus.InTransit, OrderStatus.InDelivery)]
    [InlineData(DeliveryStatus.Delivered, OrderStatus.Delivered)]
    [InlineData(DeliveryStatus.Failed, OrderStatus.DeliveryFailed)]
    [InlineData(DeliveryStatus.Cancelled, OrderStatus.Cancelled)]
    public void OrderStatusFollowsDelivery(DeliveryStatus d, OrderStatus o) => Assert.Equal(o, DeliveryRules.ToOrderStatus(d));

    [Fact]
    public void FailedDelivery_IsNotCancelledOrder() =>
        Assert.NotEqual(DeliveryRules.ToOrderStatus(DeliveryStatus.Cancelled), DeliveryRules.ToOrderStatus(DeliveryStatus.Failed));

    [Fact]
    public void EveryDeliveryStatus_HasOrderStatus_AndFinalStatusesHaveNoTransitions()
    {
        foreach (var s in Enum.GetValues<DeliveryStatus>())
        {
            _ = DeliveryRules.ToOrderStatus(s);
            if (DeliveryRules.IsFinal(s)) Assert.Empty(DeliveryRules.Next(s));
        }
    }

    [Fact]
    public void FinalStatuses() =>
        Assert.All(new[] { DeliveryStatus.Delivered, DeliveryStatus.Failed, DeliveryStatus.Cancelled }, s => Assert.True(DeliveryRules.IsFinal(s)));

    [Fact]
    public void OrderQuantities()
    {
        Assert.Equal("EMPTY_ORDER", OrderRules.ValidateQuantities(new int[0]));
        Assert.Equal("INVALID_QUANTITY", OrderRules.ValidateQuantities(new[] { 1, 0 }));
        Assert.Equal("INVALID_QUANTITY", OrderRules.ValidateQuantities(new[] { 100 }));
        Assert.Null(OrderRules.ValidateQuantities(new[] { 1, 99 }));
        Assert.Equal(1000m, OrderRules.Total(new[] { (300m, 2), (400m, 1) }));
    }

    [Fact]
    public void ClientCancelAndEditWindow()
    {
        Assert.True(OrderRules.CanClientCancel(DeliveryStatus.Created));
        Assert.True(OrderRules.CanClientCancel(DeliveryStatus.Assigned));
        Assert.False(OrderRules.CanClientCancel(DeliveryStatus.Accepted));
        Assert.False(OrderRules.CanEdit(DeliveryStatus.InTransit));
    }

    [Fact]
    public void CourierStateRules()
    {
        Assert.True(CourierRules.CanAssign(CourierState.Available));
        Assert.False(CourierRules.CanAssign(CourierState.Offline));
        Assert.Equal("COURIER_BUSY", CourierRules.ValidateStateChange(CourierState.Busy, CourierState.Offline, true));
        Assert.Equal("FORBIDDEN", CourierRules.ValidateStateChange(CourierState.Available, CourierState.Blocked, false));
        Assert.Null(CourierRules.ValidateStateChange(CourierState.Available, CourierState.Offline, false));
    }

    [Fact]
    public void CustomerRulesWork()
    {
        Assert.Equal("INVALID_NAME", CustomerRules.ValidateName(" "));
        Assert.Equal("INVALID_ADDRESS", CustomerRules.ValidateAddress("Волгоград", "", "1"));
        Assert.Equal("г. Волгоград, ул. Мира, д. 15, кв. 24", CustomerRules.FormatAddress("Волгоград", "Мира", "15", "24"));
        Assert.Equal("г. Волгоград, ул. Мира, д. 15", CustomerRules.FormatAddress("Волгоград", "Мира", "15", null));
    }
}
