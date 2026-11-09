using Contracts;

namespace CourierModule;

/// <summary>Правила доступности курьеров: Available, Busy, Offline, Blocked.</summary>
public static class CourierRules
{
    public static bool CanAssign(CourierState s) => s == CourierState.Available;

    /// <summary>Проверка ручной смены состояния. Возвращает код ошибки или null.</summary>
    public static string? ValidateStateChange(CourierState from, CourierState to, bool byOperator)
    {
        if (from == to) return null;
        if (from == CourierState.Busy) return "COURIER_BUSY";
        if (to == CourierState.Busy) return "INVALID_COURIER_STATE";
        if ((to == CourierState.Blocked || from == CourierState.Blocked) && !byOperator) return "FORBIDDEN";
        return null;
    }
}
