namespace Contracts.Delivery;

public class CreateDeliveryRequest
{
    public Guid deliveryId { get; set; }
    public Guid CustomerId { get; set; }
    public string Adress { get; set; } = string.Empty;
};
