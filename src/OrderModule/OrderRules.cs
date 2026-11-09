using Contracts;
using DeliveryModule;

namespace OrderModule;

/// <summary>Бизнес-правила заказа: состав, редактирование, отмена клиентом.</summary>
public static class OrderRules
{
    public const int MaxQuantity = 99;

    /// <summary>Проверка количеств позиций. Возвращает код ошибки или null.</summary>
    public static string? ValidateQuantities(IReadOnlyCollection<int>? quantities)
    {
        if (quantities is null || quantities.Count == 0) return "EMPTY_ORDER";
        return quantities.Any(q => q < 1 || q > MaxQuantity) ? "INVALID_QUANTITY" : null;
    }

    /// <summary>Заказ можно менять, пока курьер не принял его в работу.</summary>
    public static bool CanEdit(DeliveryStatus s) => s is DeliveryStatus.Created or DeliveryStatus.Assigned;

    /// <summary>Клиент может отменить заказ только до передачи в доставку.</summary>
    public static bool CanClientCancel(DeliveryStatus s) => s is DeliveryStatus.Created or DeliveryStatus.Assigned;

    public static decimal Total(IEnumerable<(decimal Price, int Quantity)> lines) =>
        lines.Sum(l => l.Price * l.Quantity);

    public static OrderStatus StatusFor(DeliveryStatus s) => DeliveryRules.ToOrderStatus(s);
}
