namespace OrderModule;

/// <summary>
/// Деньги хранятся и считаются в целых копейках (long): без ошибок двоичной арифметики double.
/// Наружу (API, WPF) цены по-прежнему отдаются как decimal в рублях.
/// </summary>
public static class Money
{
    public const decimal MaxPrice = 1_000_000m;

    /// <summary>Рубли → копейки. Округление до копейки: от нуля (0.005 → 0.01).</summary>
    public static long ToKopecks(decimal rubles) =>
        (long)Math.Round(rubles * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromKopecks(long kopecks) => kopecks / 100m;

    /// <summary>Цена товара допустима: от 0 до MaxPrice включительно (после округления до копейки).</summary>
    public static bool IsValidPrice(decimal rubles) => rubles >= 0 && rubles <= MaxPrice;

    /// <summary>Сумма строк (цена в копейках × количество). Переполнение long даёт исключение, а не искажение суммы.</summary>
    public static long Total(IEnumerable<(long PriceKopecks, int Quantity)> lines)
    {
        checked { return lines.Sum(l => l.PriceKopecks * l.Quantity); }
    }
}
