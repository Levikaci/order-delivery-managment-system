namespace Contracts.Delivery;

public class CreateDeliveryRequest
{
    public Guid DeliveryId { get; set; }
    public Guid CustomerId { get; set; }
    public string Address { get; set; } = string.Empty;
};
