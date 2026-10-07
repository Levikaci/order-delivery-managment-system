using Contracts.Delivery;
using Microsoft.Extensions.Logging;

namespace OrderModule;

public class OrderService
{
    private readonly IDeliveryService _deliveryService;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IDeliveryService deliveryService,
        ILogger<OrderService> logger)
    {
        _deliveryService = deliveryService;
        _logger = logger;
    }

    public async Task<bool> CreateOrderAsync(
        Guid orderId,
        Guid customerId,
        string address)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["OrderId"] = orderId,
            ["CustomerId"] = customerId
        });

        _logger.LogInformation("Создание заказа");

        var deliveryRequest = new CreateDeliveryRequest
        {
            DeliveryId = Guid.NewGuid(),
            CustomerId = customerId,
            Address = address
        };

        var deliveryCreated =
            await _deliveryService.CreateAsync(deliveryRequest);

        if (!deliveryCreated)
        {
            _logger.LogWarning(
                "Заказ не создан: не удалось создать доставку");

            return false;
        }

        _logger.LogInformation(
            "Заказ успешно создан вместе с доставкой");

        return true;
    }
}
