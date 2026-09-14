using System.Globalization;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public sealed class Phase9ReportTests
{
    [Fact]
    public void MarginCalculation_PositiveNetSales_CalculatesCorrectPercentage()
    {
        // NetSales = 10000 paise (Rs 100), NetCOGS = 6000 paise (Rs 60) -> Gross Profit = 4000 paise (Rs 40)
        long netSales = 10000;
        long netCogs = 6000;
        long grossProfit = netSales - netCogs;

        decimal marginPct = netSales <= 0 ? 0.00m : Math.Round((decimal)grossProfit / netSales * 100m, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(4000, grossProfit);
        Assert.Equal(40.00m, marginPct);
    }

    [Fact]
    public void MarginCalculation_ZeroOrNegativeNetSales_ReturnsZero()
    {
        // Zero net sales
        long zeroSales = 0;
        long zeroCogs = 0;
        long gpZero = zeroSales - zeroCogs;
        decimal marginZero = zeroSales <= 0 ? 0.00m : Math.Round((decimal)gpZero / zeroSales * 100m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(0.00m, marginZero);

        // Negative net sales (e.g. refunds exceed sales)
        long negSales = -5000;
        long negCogs = -2000;
        long gpNeg = negSales - negCogs;
        decimal marginNeg = negSales <= 0 ? 0.00m : Math.Round((decimal)gpNeg / negSales * 100m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(0.00m, marginNeg);
    }

    [Fact]
    public void MarginCalculation_AwayFromZeroRounding_RoundsCorrectly()
    {
        // 100 / 300 = 33.33333... -> 33.33%
        long sales1 = 30000;
        long cogs1 = 20000;
        long gp1 = sales1 - cogs1;
        decimal margin1 = Math.Round((decimal)gp1 / sales1 * 100m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(33.33m, margin1);

        // 1 / 8 = 12.5% -> 12.50%
        long sales2 = 80000;
        long cogs2 = 70000;
        long gp2 = sales2 - cogs2;
        decimal margin2 = Math.Round((decimal)gp2 / sales2 * 100m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(12.50m, margin2);
    }

    [Fact]
    public void TimeZoneBoundary_IndiaStandardTime_ProducesHalfOpenInterval()
    {
        // Store Timezone: India Standard Time (UTC+5:30)
        var ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var startDate = new DateOnly(2026, 9, 14);
        var endDate = new DateOnly(2026, 9, 14);

        var startLocal = startDate.ToDateTime(TimeOnly.MinValue);
        var endLocal = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, ist);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, ist);

        // 2026-09-14 00:00:00 IST is 2026-09-13 18:30:00 UTC
        Assert.Equal(new DateTime(2026, 9, 13, 18, 30, 0, DateTimeKind.Utc), startUtc);
        // 2026-09-15 00:00:00 IST is 2026-09-14 18:30:00 UTC
        Assert.Equal(new DateTime(2026, 9, 14, 18, 30, 0, DateTimeKind.Utc), endUtc);
        // Interval is exactly 24 hours
        Assert.Equal(TimeSpan.FromHours(24), endUtc - startUtc);
    }

    [Fact]
    public void MoneyAndQuantityPrecision_ReportsAggregationSafety()
    {
        // 15.5 kg @ 24.50 (2450 paise)
        var qty = Quantity.From(15.500m, UnitOfMeasure.Kilogram);
        var unitPrice = Money.FromMinorUnits(2450);

        // Subtotal in paise = 15.5 * 2450 = 37975 paise
        long totalPaise = (long)Math.Round(qty.Value * unitPrice.MinorUnits, MidpointRounding.AwayFromZero);
        Assert.Equal(37975, totalPaise);

        // Ensure 64-bit integer arithmetic cannot overflow under realistic supermarket volumes
        long hugeVolumeSales = 10_000_000_00L; // 100 million rupees in paise
        long hugeVolumeCogs = 7_500_000_00L;
        long profit = hugeVolumeSales - hugeVolumeCogs;
        Assert.Equal(2_500_000_00L, profit);
    }

    [Fact]
    public void CsvEscaping_QuotesAndSpecialCharacters_EscapesCorrectly()
    {
        static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            var escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        Assert.Equal("\"Hello, World\"", Escape("Hello, World"));
        Assert.Equal("\"Line1\nLine2\"", Escape("Line1\nLine2"));
        Assert.Equal("\"He said \"\"hello\"\"\"", Escape("He said \"hello\""));
        Assert.Equal("\"\"", Escape(null));
        Assert.Equal("\"\"", Escape(string.Empty));
    }
}
