using DeliveryModule;
using Microsoft.Extensions.Logging.Abstractions;
using OrderModule;
using Xunit;

namespace OrderModule.Tests;

public class OrderServiceTests
{
    [Fact]
    public async Task CreateOrderAsync_ShouldReturnTrue()
    {
        var deliveryService =
            new DeliveryService(NullLogger<DeliveryService>.Instance);

        var orderService = new OrderService(
            deliveryService,
            NullLogger<OrderService>.Instance);

        var result = await orderService.CreateOrderAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ул. Тестовая, 1");

        Assert.True(result);
    }

    [Fact]
    public async Task CreateOrderAsync_WithEmptyAddress_ShouldReturnFalse()
    {
        var deliveryService =
            new DeliveryService(NullLogger<DeliveryService>.Instance);

        var orderService = new OrderService(
            deliveryService,
            NullLogger<OrderService>.Instance);

        var result = await orderService.CreateOrderAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "");

        Assert.False(result);
    }

    [Fact]
    public async Task CreateOrderAsync_WithEmptyOrderId_ShouldReturnFalse()
    {
        var deliveryService =
            new DeliveryService(NullLogger<DeliveryService>.Instance);

        var orderService = new OrderService(
            deliveryService,
            NullLogger<OrderService>.Instance);

        var result = await orderService.CreateOrderAsync(
            Guid.Empty,
            Guid.NewGuid(),
            "ул. Тестовая, 3");

        Assert.False(result);
    }
}
