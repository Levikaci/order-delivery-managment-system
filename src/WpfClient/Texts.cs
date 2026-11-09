namespace WpfClient;

/// <summary>Русские подписи статусов и правила переходов (зеркало DeliveryRules на сервере).</summary>
public static class Texts
{
    public static readonly Dictionary<string, string> Delivery = new()
    {
        ["Created"] = "Создана", ["Assigned"] = "Назначена", ["Accepted"] = "Принята курьером",
        ["InTransit"] = "В пути", ["Delivered"] = "Доставлена", ["Failed"] = "Не удалась", ["Cancelled"] = "Отменена",
    };
    public static readonly Dictionary<string, string> Order = new()
    {
        ["Created"] = "Создан", ["Confirmed"] = "Подтверждён", ["Preparing"] = "Готовится",
        ["InDelivery"] = "В доставке", ["Delivered"] = "Доставлен", ["Cancelled"] = "Отменён",
    };
    public static readonly Dictionary<string, string> Courier = new()
    {
        ["Available"] = "Свободен", ["Busy"] = "Занят", ["Offline"] = "Не на линии", ["Blocked"] = "Заблокирован",
    };
    public static readonly Dictionary<string, string[]> Next = new()
    {
        ["Created"] = new[] { "Assigned", "Cancelled" },
        ["Assigned"] = new[] { "Accepted", "Failed", "Cancelled" },
        ["Accepted"] = new[] { "InTransit", "Failed" },
        ["InTransit"] = new[] { "Delivered", "Failed" },
    };
    public static string D(string? s) => s != null && Delivery.TryGetValue(s, out var v) ? v : s ?? "";
    public static string O(string? s) => s != null && Order.TryGetValue(s, out var v) ? v : s ?? "";
    public static string C(string? s) => s != null && Courier.TryGetValue(s, out var v) ? v : s ?? "";
}
