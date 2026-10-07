using Contracts.Delivery;
using Microsoft.Extensions.Logging;

namespace DeliveryModule;

public class DeliveryService : IDeliveryService
{
    private readonly ILogger<DeliveryService> _logger;
    private readonly Dictionary<Guid, CreateDeliveryRequest> _deliveries = new();

    public DeliveryService(ILogger<DeliveryService> logger)
    {
        _logger = logger;
    }

    public Task<bool> CreateAsync(CreateDeliveryRequest request)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["DeliveryId"] = request.DeliveryId,
            ["CustomerId"] = request.CustomerId
        });

        if (_deliveries.ContainsKey(request.DeliveryId))
        {
            _logger.LogWarning(
                "Создание доставки отклонено: доставка уже существует");

            return Task.FromResult(false);
        }

        _deliveries[request.DeliveryId] = request;

        _logger.LogInformation(
            "Доставка успешно создана");

        return Task.FromResult(true);
    }
}
