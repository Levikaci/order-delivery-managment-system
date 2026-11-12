using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// <summary>
/// RequestId — необязательный ключ идемпотентности в рамках клиента: повтор запроса с тем же ключом и теми же данными
/// возвращает уже созданный заказ, с другими данными — 409 IDEMPOTENCY_CONFLICT.
/// </summary>
public record CreateOrderRequest(Guid CustomerId, List<ItemRequest>? Items, AddressDto? DeliveryAddress = null,
    Guid? AddressId = null, string? Comment = null, bool SaveAddress = false,
    Guid? RequestId = null)
{
    /// <summary>
    /// Отпечаток значимых полей запроса (SHA-256 нормализованного JSON): клиент, товары с количеством
    /// (одинаковые товары суммируются, порядок не важен), адрес (сохранённый или введённый), комментарий,
    /// признак сохранения адреса. Пробелы по краям и пустые значения не влияют, как и при сохранении заказа.
    /// RequestId в отпечаток не входит.
    /// </summary>
    public byte[] Fingerprint()
    {
        static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var items = (Items ?? new()).GroupBy(i => i.ProductId)
            .Select(g => new { product = g.Key, quantity = g.Sum(i => (long)i.Quantity) })
            .OrderBy(i => i.product).ToList();
        // при выбранном сохранённом адресе введённый адрес и флаг сохранения сервер не использует
        object? address = AddressId is Guid id ? new { id }
            : DeliveryAddress is { } a ? new { city = Norm(a.City), street = Norm(a.Street), house = Norm(a.House), apartment = Norm(a.Apartment) }
            : null;
        var canonical = JsonSerializer.Serialize(new
        {
            v = 1,
            customer = CustomerId,
            items,
            address,
            comment = Norm(Comment),
            saveAddress = AddressId is null && DeliveryAddress is not null && SaveAddress,
        });
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }
}
/// <summary>Comment: null — не менять, пустая строка — очистить.</summary>
public record UpdateOrderRequest(List<ItemRequest>? Items, AddressDto? DeliveryAddress = null, Guid? AddressId = null, string? Comment = null);
public record CreateDeliveryBody(Guid OrderId);
public record AssignRequest(Guid? CourierId);
public record StatusRequest(DeliveryStatus Status, string? Comment = null);
public record CourierStateRequest(CourierState State);
public record ProductRequest(string Name, decimal Price);
public record AddressRequest(string? Label, string City, string Street, string House, string? Apartment);
public record LoginRequest(string Login, string Password);
/// <summary>Самостоятельная регистрация клиента: имя, необязательный телефон, логин и пароль.</summary>
public record RegisterRequest(string Name, string? Phone, string Login, string Password);
public record ChangePasswordRequest(string OldPassword, string NewPassword);

public record StatusChange(DeliveryStatus Status, DateTime At, string? Comment, Guid? ActorId = null);
public record OrderStatusChange(OrderStatus Status, DateTime At, string? Comment, Guid? ActorId = null);

/// <summary>Изменение заказа: Field = Items | Address | Comment; ActorId — users.id автора.</summary>
public record OrderChange(DateTime At, Guid? ActorId, string? ActorName, string Field, string? OldValue, string? NewValue);
public record OrderItemDto(Guid ProductId, string Name, decimal Price, int Quantity);

/// <summary>Сессия пользователя: роль client / operator / courier и связанная запись.</summary>
public record Session(string Role, Guid? RefId, string Name, Guid? UserId = null);

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
    public List<OrderChange> Changes { get; } = new();
}
