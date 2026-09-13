using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public class CatalogTests
{
    private static readonly string[] SkuErrorCodes = ["INVALID_SKU", "INVALID_SKU_FORMAT"];

    [Theory]
    [InlineData("SKU123")]
    [InlineData("ITEM-99")]
    [InlineData("PROD_A_B")]
    [InlineData("A")]
    public void Product_ValidSku_ShouldSucceed(string validSku)
    {
        var product = new Product(
            Guid.NewGuid(),
            validSku,
            "Test Product",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(1000),
            Money.FromMinorUnits(1500),
            taxCategoryId: 1);

        Assert.Equal(validSku, product.SKU);
        Assert.True(product.IsActive);
    }

    [Theory]
    [InlineData("sku-lowercase")]
    [InlineData("SKU WITH SPACES")]
    [InlineData("SKU@SPECIAL!")]
    [InlineData("")]
    [InlineData("   ")]
    public void Product_InvalidSku_ShouldThrowDomainException(string invalidSku)
    {
        var ex = Assert.Throws<DomainException>(() =>
            new Product(
                Guid.NewGuid(),
                invalidSku,
                "Test Product",
                UnitOfMeasure.Piece,
                Money.FromMinorUnits(1000),
                Money.FromMinorUnits(1500),
                taxCategoryId: 1));

        Assert.Contains(ex.ErrorCode, SkuErrorCodes);
    }

    [Fact]
    public void Product_LossLeaderPricing_ShouldBeAllowed()
    {
        // Cost is 20.00 (2000 minor units), Selling price is 15.00 (1500 minor units) - allowed loss-leader
        var product = new Product(
            Guid.NewGuid(),
            "LOSS-LEADER-1",
            "Discounted Item",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(2000),
            Money.FromMinorUnits(1500),
            taxCategoryId: 1);

        Assert.Equal(2000, product.CostPrice.MinorUnits);
        Assert.Equal(1500, product.SellingPrice.MinorUnits);
    }

    [Fact]
    public void Product_ZeroCostAndSellingPrice_ShouldBeAllowed()
    {
        var product = new Product(
            Guid.NewGuid(),
            "FREE-SAMPLE-1",
            "Free Sample",
            UnitOfMeasure.Piece,
            Money.Zero(),
            Money.Zero(),
            taxCategoryId: 1);

        Assert.Equal(0, product.CostPrice.MinorUnits);
        Assert.Equal(0, product.SellingPrice.MinorUnits);
    }

    [Fact]
    public void Product_NegativeCostOrSellingPrice_ShouldThrowDomainException()
    {
        Assert.Throws<DomainException>(() =>
            new Product(
                Guid.NewGuid(),
                "NEG-COST",
                "Negative Cost",
                UnitOfMeasure.Piece,
                Money.FromMinorUnits(-100),
                Money.FromMinorUnits(100),
                taxCategoryId: 1));

        Assert.Throws<DomainException>(() =>
            new Product(
                Guid.NewGuid(),
                "NEG-PRICE",
                "Negative Price",
                UnitOfMeasure.Piece,
                Money.FromMinorUnits(100),
                Money.FromMinorUnits(-100),
                taxCategoryId: 1));
    }

    [Fact]
    public void Product_PrimaryBarcode_PromotionAndDemotion_ShouldWorkAtomically()
    {
        var product = new Product(
            Guid.NewGuid(),
            "BARCODE-PROD",
            "Barcode Product",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);

        // First barcode added as primary
        var b1 = product.AddBarcode(new Barcode("111111111111"), isPrimary: true);
        Assert.True(b1.IsPrimary);
        Assert.Single(product.Barcodes);

        // Second barcode added as primary -> demotes first
        var b2 = product.AddBarcode(new Barcode("222222222222"), isPrimary: true);
        Assert.False(b1.IsPrimary);
        Assert.True(b2.IsPrimary);
        Assert.Equal(2, product.Barcodes.Count);

        // Third barcode added as non-primary -> b2 remains primary
        var b3 = product.AddBarcode(new Barcode("333333333333"), isPrimary: false);
        Assert.False(b1.IsPrimary);
        Assert.True(b2.IsPrimary);
        Assert.False(b3.IsPrimary);
        Assert.Equal(3, product.Barcodes.Count);

        // Duplicate barcode on same product rejected
        Assert.Throws<DomainException>(() => product.AddBarcode(new Barcode("111111111111"), isPrimary: false));
    }

    [Fact]
    public void CategoryAndBrand_NameConstraints_ShouldBeEnforced()
    {
        var cat = new Category(1, "Dairy");
        Assert.Equal("Dairy", cat.Name);
        Assert.True(cat.IsActive);

        cat.Deactivate();
        Assert.False(cat.IsActive);
        cat.Activate();
        Assert.True(cat.IsActive);

        Assert.Throws<DomainException>(() => new Category(2, ""));
        Assert.Throws<DomainException>(() => new Category(3, new string('A', 101)));

        var brand = new Brand(1, "Amul");
        Assert.Equal("Amul", brand.Name);
        Assert.True(brand.IsActive);

        Assert.Throws<DomainException>(() => new Brand(2, ""));
        Assert.Throws<DomainException>(() => new Brand(3, new string('B', 101)));
    }
}