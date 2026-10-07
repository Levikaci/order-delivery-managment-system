using Contracts.Delivery;

namespace Contracts.Delivery;

public interface IDeliveryService
{
    Task<bool> CreateAsync(CreateDeliveryRequest deliveryRequest);
}
