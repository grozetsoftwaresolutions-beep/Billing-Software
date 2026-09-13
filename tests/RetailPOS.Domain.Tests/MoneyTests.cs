using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void FromMinorUnits_WithINR_ShouldStorePaiseCorrectly()
    {
        var money = Money.FromMinorUnits(12345); // 12345 paise = ?123.45
        Assert.Equal(12345, money.MinorUnits);
        Assert.Equal(123.45m, money.AmountInMajorUnits);
        Assert.Equal("?123.45", money.ToString());
    }

    [Fact]
    public void FromDecimal_WithINR_ShouldConvertAndRoundCorrectly()
    {
        var money100 = Money.FromDecimal(100.00m);
        Assert.Equal(10000, money100.MinorUnits);

        var money123 = Money.FromDecimal(123.45m);
        Assert.Equal(12345, money123.MinorUnits);
    }

    [Theory]
    [InlineData(0.005, 1)]   // 0.005 -> 0.5 paise -> rounds away from zero to 1 paise
    [InlineData(0.015, 2)]   // 0.015 -> 1.5 paise -> rounds away from zero to 2 paise
    [InlineData(0.025, 3)]   // 0.025 -> 2.5 paise -> rounds away from zero to 3 paise
    [InlineData(10.004, 1000)] // 1000.4 paise -> rounds to 1000 paise
    [InlineData(10.005, 1001)] // 1000.5 paise -> rounds to 1001 paise
    public void RoundingBoundaries_ShouldFollowMidpointAwayFromZero(decimal amount, long expectedPaise)
    {
        var money = Money.FromDecimal(amount);
        Assert.Equal(expectedPaise, money.MinorUnits);
    }

    [Fact]
    public void ZeroAndLargeValues_ShouldBeSupported()
    {
        var zero = Money.Zero();
        Assert.True(zero.IsZero);
        Assert.Equal(0, zero.MinorUnits);

        // ?100,000,000.00 (10 crore) = 10,000,000,000 paise
        var large = Money.FromDecimal(100_000_000.00m);
        Assert.Equal(10_000_000_000L, large.MinorUnits);
        Assert.Equal(100_000_000.00m, large.AmountInMajorUnits);
    }

    [Fact]
    public void Arithmetic_AdditionAndSubtraction_ShouldBeExact()
    {
        var a = Money.FromDecimal(50.25m); // 5025 paise
        var b = Money.FromDecimal(49.75m); // 4975 paise

        var sum = a + b;
        Assert.Equal(10000, sum.MinorUnits);
        Assert.Equal(100.00m, sum.AmountInMajorUnits);

        var diff = a - b;
        Assert.Equal(50, diff.MinorUnits);
        Assert.Equal(0.50m, diff.AmountInMajorUnits);
    }

    [Fact]
    public void Arithmetic_MultiplicationAndDivision_ShouldBeExact()
    {
        var price = Money.FromDecimal(19.99m); // 1999 paise
        var total = price * 3m; // 5997 paise
        Assert.Equal(5997, total.MinorUnits);
        Assert.Equal(59.97m, total.AmountInMajorUnits);

        var third = total / 3m;
        Assert.Equal(1999, third.MinorUnits);
    }

    [Fact]
    public void CurrencyMismatch_ShouldThrowDomainException()
    {
        var usd = new Currency("USD", "$", 100, 2);
        var inrMoney = Money.FromDecimal(10m, Currency.INR);
        var usdMoney = Money.FromDecimal(10m, usd);

        Assert.Throws<DomainException>(() => inrMoney + usdMoney);
    }
}
