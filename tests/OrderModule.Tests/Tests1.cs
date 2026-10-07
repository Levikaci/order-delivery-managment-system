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

        var orderService =
            new OrderService(
                deliveryService,
                NullLogger<OrderService>.Instance);

        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var result = await orderService.CreateOrderAsync(
            orderId,
            customerId,
            "ул. Тестовая, 1");

        Assert.True(result);
    }
}
