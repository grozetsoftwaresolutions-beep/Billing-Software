using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Services;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public class PricingAndTaxTests
{
    private readonly PricingCalculator _calculator = new();
    private readonly TaxCategory _gst5 = new(1, "GST_5", "GST 5%", false, [
        new TaxComponent("CGST", 2.5m),
        new TaxComponent("SGST", 2.5m)
    ]);
    private readonly TaxCategory _gst18 = new(2, "GST_18", "GST 18%", false, [
        new TaxComponent("CGST", 9.0m),
        new TaxComponent("SGST", 9.0m)
    ]);

    [Fact]
    public void TaxExclusive_ShouldCalculateTaxOnTopOfBasePrice()
    {
        // 2 items @ ?100 each = ?200 base. 5% tax = ?10. Line total = ?210.
        var qty = Quantity.From(2m, UnitOfMeasure.Piece);
        var unitPrice = Money.FromDecimal(100.00m);

        var result = _calculator.CalculateLine(qty, unitPrice, _gst5, PricingModel.TaxExclusive);

        Assert.Equal(20000, result.GrossAmount.MinorUnits); // ?200.00
        Assert.Equal(0, result.DiscountAmount.MinorUnits);
        Assert.Equal(20000, result.TaxableAmount.MinorUnits); // ?200.00
        Assert.Equal(1000, result.TaxAmount.MinorUnits); // ?10.00 (5%)
        Assert.Equal(21000, result.LineTotal.MinorUnits); // ?210.00

        // Component breakdown check: CGST = ?5.00, SGST = ?5.00
        var cgst = result.TaxComponents.First(c => c.ComponentName == "CGST");
        var sgst = result.TaxComponents.First(c => c.ComponentName == "SGST");
        Assert.Equal(500, cgst.Amount.MinorUnits);
        Assert.Equal(500, sgst.Amount.MinorUnits);
    }

    [Fact]
    public void TaxInclusive_ShouldExtractTaxFromGrossSellingPrice()
    {
        // 1 item @ ?105 gross (tax-inclusive 5% GST).
        // Taxable = 105 / 1.05 = ?100.00. Tax = ?5.00. Line Total = ?105.00.
        var qty = Quantity.From(1m, UnitOfMeasure.Piece);
        var unitPrice = Money.FromDecimal(105.00m);

        var result = _calculator.CalculateLine(qty, unitPrice, _gst5, PricingModel.TaxInclusive);

        Assert.Equal(10500, result.GrossAmount.MinorUnits);
        Assert.Equal(10000, result.TaxableAmount.MinorUnits); // ?100.00
        Assert.Equal(500, result.TaxAmount.MinorUnits); // ?5.00
        Assert.Equal(10500, result.LineTotal.MinorUnits); // ?105.00
    }

    [Fact]
    public void ZeroTaxAndExempt_ShouldProduceZeroTaxAmount()
    {
        var qty = Quantity.From(3m, UnitOfMeasure.Piece);
        var unitPrice = Money.FromDecimal(50.00m);

        var result = _calculator.CalculateLine(qty, unitPrice, TaxCategory.Exempt, PricingModel.TaxExclusive);

        Assert.Equal(15000, result.GrossAmount.MinorUnits);
        Assert.Equal(0, result.TaxAmount.MinorUnits);
        Assert.Equal(15000, result.LineTotal.MinorUnits);
    }

    [Fact]
    public void PercentageDiscount_ShouldReduceTaxableAmountAndTax()
    {
        // 1 item @ ?100. 10% discount = ?10 discount. Net base = ?90.
        // 18% tax on ?90 = ?16.20. Line Total = ?106.20.
        var qty = Quantity.From(1m, UnitOfMeasure.Piece);
        var unitPrice = Money.FromDecimal(100.00m);

        var result = _calculator.CalculateLine(
            qty, unitPrice, _gst18, PricingModel.TaxExclusive,
            discountType: DiscountType.Percentage, discountValue: 10m);

        Assert.Equal(10000, result.GrossAmount.MinorUnits);
        Assert.Equal(1000, result.DiscountAmount.MinorUnits); // ?10.00
        Assert.Equal(9000, result.TaxableAmount.MinorUnits);  // ?90.00
        Assert.Equal(1620, result.TaxAmount.MinorUnits);      // ?16.20
        Assert.Equal(10620, result.LineTotal.MinorUnits);     // ?106.20
    }

    [Fact]
    public void DiscountGreaterThanLineAmount_ShouldBeSafelyCappedAtSubtotal()
    {
        // ?50 item with fixed discount of ?100 -> capped at ?50 (100% discount, zero payable)
        var qty = Quantity.From(1m, UnitOfMeasure.Piece);
        var unitPrice = Money.FromDecimal(50.00m);

        var result = _calculator.CalculateLine(
            qty, unitPrice, _gst5, PricingModel.TaxExclusive,
            discountType: DiscountType.FixedAmount, discountValue: 100m);

        Assert.Equal(5000, result.DiscountAmount.MinorUnits); // capped at ?50.00
        Assert.Equal(0, result.TaxableAmount.MinorUnits);
        Assert.Equal(0, result.TaxAmount.MinorUnits);
        Assert.Equal(0, result.LineTotal.MinorUnits);
    }

    [Fact]
    public void MultiLineInvoice_WithRoundingToWholeRupee_ShouldReconcileAccurately()
    {
        // Line 1: 1 x ?99.90 (Tax exclusive 5% GST = ?5.00 tax => ?104.90)
        // Line 2: 2 x ?49.50 (Tax exclusive 18% GST = ?17.82 tax => ?116.82)
        // Total lines sum = ?104.90 + ?116.82 = ?221.72
        // Rounded to whole rupee: ?222.00 (Rounding adjustment: +?0.28)
        var line1 = _calculator.CalculateLine(
            Quantity.From(1m, UnitOfMeasure.Piece),
            Money.FromDecimal(99.90m),
            _gst5,
            PricingModel.TaxExclusive);

        var line2 = _calculator.CalculateLine(
            Quantity.From(2m, UnitOfMeasure.Piece),
            Money.FromDecimal(49.50m),
            _gst18,
            PricingModel.TaxExclusive);

        var invoice = _calculator.CalculateInvoice([line1, line2], roundToWholeRupee: true);

        Assert.Equal(22200, invoice.FinalPayable.MinorUnits); // ?222.00
        Assert.Equal(28, invoice.RoundingAdjustment.MinorUnits); // +?0.28
        Assert.Equal(2, invoice.Lines.Count);
    }
}
