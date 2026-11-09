using Contracts;

namespace DeliveryModule;

/// <summary>Бизнес-правила доставки: допустимые переходы статусов и связь со статусом заказа.</summary>
public static class DeliveryRules
{
    private static readonly Dictionary<DeliveryStatus, DeliveryStatus[]> Transitions = new()
    {
        [DeliveryStatus.Created] = new[] { DeliveryStatus.Assigned, DeliveryStatus.Cancelled },
        [DeliveryStatus.Assigned] = new[] { DeliveryStatus.Accepted, DeliveryStatus.Failed, DeliveryStatus.Cancelled },
        [DeliveryStatus.Accepted] = new[] { DeliveryStatus.InTransit, DeliveryStatus.Failed },
        [DeliveryStatus.InTransit] = new[] { DeliveryStatus.Delivered, DeliveryStatus.Failed },
    };

    public static bool IsFinal(DeliveryStatus s) =>
        s is DeliveryStatus.Delivered or DeliveryStatus.Failed or DeliveryStatus.Cancelled;

    public static bool CanTransition(DeliveryStatus from, DeliveryStatus to) =>
        Transitions.TryGetValue(from, out var next) && next.Contains(to);

    public static IReadOnlyList<DeliveryStatus> Next(DeliveryStatus from) =>
        Transitions.TryGetValue(from, out var n) ? n : Array.Empty<DeliveryStatus>();

    /// <summary>Статус заказа, который соответствует статусу доставки.</summary>
    public static OrderStatus ToOrderStatus(DeliveryStatus s) => s switch
    {
        DeliveryStatus.Created => OrderStatus.Created,
        DeliveryStatus.Assigned => OrderStatus.Confirmed,
        DeliveryStatus.Accepted => OrderStatus.Preparing,
        DeliveryStatus.InTransit => OrderStatus.InDelivery,
        DeliveryStatus.Delivered => OrderStatus.Delivered,
        _ => OrderStatus.Cancelled,
    };
}
