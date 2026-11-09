using CustomerModule;

namespace ApiHost;

public record Customer(Guid Id, string Name, string Phone);
public record Courier(Guid Id, string Name, string Phone, CourierState State)
{
    public bool Available => State == CourierState.Available;
}
public record Product(Guid Id, string Name, decimal Price);
public record SavedAddress(Guid Id, Guid CustomerId, string Label, string City, string Street, string House, string? Apartment)
{
    public string Text => CustomerRules.FormatAddress(City, Street, House, Apartment);
}
public record AddressDto(string City, string Street, string House, string? Apartment = null)
{
    public override string ToString() => CustomerRules.FormatAddress(City, Street, House, Apartment);
}

public record NewPerson(string Name, string? Phone, string? Login = null, string? Password = null);
public record ItemRequest(Guid ProductId, int Quantity);
public record CreateOrderRequest(Guid CustomerId, List<ItemRequest>? Items, AddressDto? DeliveryAddress = null,
    Guid? AddressId = null, string? Comment = null, bool SaveAddress = false);
public record UpdateOrderRequest(List<ItemRequest>? Items, AddressDto? DeliveryAddress = null, Guid? AddressId = null, string? Comment = null);
public record CreateDeliveryBody(Guid OrderId);
public record AssignRequest(Guid? CourierId);
public record StatusRequest(DeliveryStatus Status, string? Comment = null);
public record CourierStateRequest(CourierState State);
public record ProductRequest(string Name, decimal Price);
public record AddressRequest(string? Label, string City, string Street, string House, string? Apartment);
public record LoginRequest(string Login, string Password);

public record StatusChange(DeliveryStatus Status, DateTime At, string? Comment);
public record OrderStatusChange(OrderStatus Status, DateTime At, string? Comment);
public record OrderItemDto(Guid ProductId, string Name, decimal Price, int Quantity);

/// <summary>Сессия пользователя: роль client / operator / courier и связанная запись.</summary>
public record Session(string Role, Guid? RefId, string Name);

public class Delivery
{
    public Guid DeliveryId { get; init; }
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public string Description { get; set; } = "";
    public string Address { get; init; } = "";
    public string? Comment { get; init; }
    public Guid? CourierId { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Created;
    public OrderStatus OrderStatus { get; set; } = OrderStatus.Created;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? FailureReason { get; set; }
    public decimal Total { get; set; }
    public List<OrderItemDto> Items { get; } = new();
    public List<StatusChange> History { get; } = new();
    public List<OrderStatusChange> OrderHistory { get; } = new();
}
