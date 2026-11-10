using OrderModule;

namespace OrderModule.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData(590, 59000)]
    [InlineData(0.01, 1)]
    [InlineData(0.1, 10)]
    [InlineData(19.99, 1999)]
    [InlineData(1000000, 100000000)]
    public void ToKopecks_IsExact(double rubles, long kopecks) =>
        Assert.Equal(kopecks, Money.ToKopecks((decimal)rubles));

    [Theory]
    [InlineData("0.004", 0)]
    [InlineData("0.005", 1)]   // от нуля, а не банковское округление
    [InlineData("0.015", 2)]
    [InlineData("2.675", 268)]
    [InlineData("10.994", 1099)]
    [InlineData("10.995", 1100)]
    public void ToKopecks_RoundsHalfAwayFromZero(string rubles, long kopecks) =>
        Assert.Equal(kopecks, Money.ToKopecks(decimal.Parse(rubles, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void RoundTrip_KopecksToRubles_IsLossless()
    {
        for (long k = 0; k <= 100_000; k += 7) Assert.Equal(k, Money.ToKopecks(Money.FromKopecks(k)));
    }

    [Fact]
    public void Total_DoesNotAccumulateBinaryError()
    {
        // 0.1 * 3 в double = 0.30000000000000004
        var total = OrderRules.Total(new[] { (0.1m, 1), (0.1m, 1), (0.1m, 1) });
        Assert.Equal(0.30m, total);
        Assert.Equal(1_000_000_00L, Money.Total(Enumerable.Repeat((1_000_00L, 1), 1000)));
    }

    [Fact]
    public void Total_MultipliesByQuantity_AndSumsLines() =>
        Assert.Equal(590m * 2 + 150m + 19.99m * 3, OrderRules.Total(new[] { (590m, 2), (150m, 1), (19.99m, 3) }));

    [Fact]
    public void Total_Overflow_Throws() =>
        Assert.Throws<OverflowException>(() => Money.Total(new[] { (long.MaxValue / 2, 99) }));

    [Theory]
    [InlineData("-0.01", false)]
    [InlineData("0", true)]
    [InlineData("1000000", true)]
    [InlineData("1000000.01", false)]
    public void IsValidPrice(string rubles, bool ok) =>
        Assert.Equal(ok, Money.IsValidPrice(decimal.Parse(rubles, System.Globalization.CultureInfo.InvariantCulture)));
}
