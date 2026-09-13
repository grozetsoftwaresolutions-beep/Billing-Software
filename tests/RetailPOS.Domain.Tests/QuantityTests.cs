using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public class QuantityTests
{
    [Fact]
    public void PieceQuantity_WithInteger_ShouldSucceed()
    {
        var qty = Quantity.From(5m, UnitOfMeasure.Piece);
        Assert.Equal(5m, qty.Value);
        Assert.Equal("5 PC", qty.ToString());
    }

    [Fact]
    public void PieceQuantity_WithFraction_ShouldThrowDomainException()
    {
        // Pieces cannot have decimals
        Assert.Throws<DomainException>(() => Quantity.From(5.5m, UnitOfMeasure.Piece));
    }

    [Fact]
    public void WeightedQuantity_KgAndLitre_ShouldAllowUpTo3Decimals()
    {
        var kgQty = Quantity.From(1.234m, UnitOfMeasure.Kilogram);
        Assert.Equal(1.234m, kgQty.Value);
        Assert.Equal("1.234 KG", kgQty.ToString());

        var litreQty = Quantity.From(0.500m, UnitOfMeasure.Litre);
        Assert.Equal(0.500m, litreQty.Value);
        Assert.Equal("0.500 L", litreQty.ToString());
    }

    [Fact]
    public void WeightedQuantity_ExceedingAllowedPrecision_ShouldThrowDomainException()
    {
        // 4 decimals for Kg should throw
        Assert.Throws<DomainException>(() => Quantity.From(1.2345m, UnitOfMeasure.Kilogram));
    }

    [Fact]
    public void VerySmallQuantity_ShouldBeSupported()
    {
        var tiny = Quantity.From(0.005m, UnitOfMeasure.Kilogram); // 5 grams
        Assert.Equal(0.005m, tiny.Value);
        Assert.True(tiny.IsPositive);
    }

    [Fact]
    public void Arithmetic_ShouldWorkForSameUnit()
    {
        var a = Quantity.From(1.250m, UnitOfMeasure.Kilogram);
        var b = Quantity.From(2.750m, UnitOfMeasure.Kilogram);

        var sum = a + b;
        Assert.Equal(4.000m, sum.Value);
    }

    [Fact]
    public void Arithmetic_DifferentUnits_ShouldThrowDomainException()
    {
        var pcs = Quantity.From(2m, UnitOfMeasure.Piece);
        var kgs = Quantity.From(1.5m, UnitOfMeasure.Kilogram);

        Assert.Throws<DomainException>(() => pcs + kgs);
    }
}
