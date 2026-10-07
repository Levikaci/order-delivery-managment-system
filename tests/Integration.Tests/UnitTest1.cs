using DeliveryModule;
using Microsoft.Extensions.Logging.Abstractions;
using OrderModule;
using Xunit;

namespace Integration.Tests;

public class OrderDeliveryIntegrationTests
{
        [Fact]
        public async Task CreateDeliveryTwice_ShouldReturnFalse()
        {
            var deliveryService = new DeliveryService(
                NullLogger<DeliveryService>.Instance);

            var request = new Contracts.Delivery.CreateDeliveryRequest
            {
                DeliveryId = Guid.NewGuid(),
                CustomerId = Guid.NewGuid(),
                Address = "ул. Тестовая, 2"
            };

            var firstResult = await deliveryService.CreateAsync(request);
            var secondResult = await deliveryService.CreateAsync(request);

            Assert.True(firstResult);
            Assert.False(secondResult);
        }
    [Fact]
    public async Task CreateOrder_ShouldCreateOrderAndDelivery()
    {
        // Arrange
        var deliveryService = new DeliveryService(
            NullLogger<DeliveryService>.Instance);

        var orderService = new OrderService(
            deliveryService,
            NullLogger<OrderService>.Instance);

        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        // Act
        var result = await orderService.CreateOrderAsync(
            orderId,
            customerId,
            "ул. Тестовая, 1");

        // Assert
        Assert.True(result);
    }
}
