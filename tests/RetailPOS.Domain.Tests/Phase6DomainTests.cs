using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public class Phase6DomainTests
{
    [Fact]
    public void Store_DefaultTimeZone_IsIndiaStandardTime()
    {
        var store = new Store(1, "Test Retail Store");
        Assert.Equal("India Standard Time", store.TimeZoneId);
        Assert.True(store.CashRoundingEnabled);
    }

    [Theory]
    [InlineData("India Standard Time")]
    [InlineData("UTC")]
    [InlineData("Pacific Standard Time")]
    public void Store_ValidWindowsTimeZone_Accepted(string validTz)
    {
        var store = new Store(1, "Test Store", timeZoneId: validTz);
        Assert.Equal(validTz, store.TimeZoneId);

        store.UpdateBillingSettings(false, "India Standard Time");
        Assert.Equal("India Standard Time", store.TimeZoneId);
        Assert.False(store.CashRoundingEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Store_EmptyTimeZone_ThrowsDomainException(string? invalidTz)
    {
        var ex = Assert.Throws<DomainException>(() => Store.ValidateWindowsTimeZone(invalidTz!));
        Assert.Equal("INVALID_TIMEZONE_ID", ex.ErrorCode);
    }

    [Theory]
    [InlineData("Asia/Kolkata")]
    [InlineData("America/New_York")]
    [InlineData("Europe/London")]
    public void Store_IanaTimeZone_RejectedWithExplicitError(string ianaTz)
    {
        var ex = Assert.Throws<DomainException>(() => Store.ValidateWindowsTimeZone(ianaTz));
        Assert.Equal("INVALID_TIMEZONE_ID", ex.ErrorCode);
        Assert.Contains("IANA time zone identifiers are not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Store_NonExistentTimeZone_ThrowsDomainException()
    {
        var ex = Assert.Throws<DomainException>(() => Store.ValidateWindowsTimeZone("Fantasy/NonExistent_Zone_123"));
        Assert.Equal("INVALID_TIMEZONE_ID", ex.ErrorCode);
    }

    [Fact]
    public void PaymentRecord_Cash_ValidInvariants()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(15000);
        var tendered = Money.FromMinorUnits(20000);

        var payment = new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Cash, amount, tendered);

        Assert.Equal(PaymentMethod.Cash, payment.Method);
        Assert.Equal(amount, payment.Amount);
        Assert.Equal(tendered, payment.TenderedAmount);
        Assert.Equal(Money.FromMinorUnits(5000), payment.ChangeAmount);
    }

    [Fact]
    public void PaymentRecord_Cash_TenderedLessThanAmount_ThrowsDomainException()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(15000);
        var tendered = Money.FromMinorUnits(10000);

        var ex = Assert.Throws<DomainException>(() =>
            new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Cash, amount, tendered));
        Assert.Equal("TENDERED_AMOUNT_INSUFFICIENT", ex.ErrorCode);
    }

    [Fact]
    public void PaymentRecord_Card_ValidInvariants()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(25000);
        var payment = new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Card, amount, amount, Money.Zero(), "TXN-987654321");

        Assert.Equal(PaymentMethod.Card, payment.Method);
        Assert.Equal(amount, payment.Amount);
        Assert.Equal(amount, payment.TenderedAmount);
        Assert.Equal(Money.Zero(), payment.ChangeAmount);
        Assert.Equal("TXN-987654321", payment.ReferenceNumber);
    }

    [Fact]
    public void PaymentRecord_Card_MissingOrInvalidReference_ThrowsDomainException()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(25000);

        var ex = Assert.Throws<DomainException>(() =>
            new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Card, amount, amount, Money.Zero(), ""));
        Assert.Equal("CARD_REFERENCE_REQUIRED", ex.ErrorCode);

        var ex2 = Assert.Throws<DomainException>(() =>
            new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Card, amount, amount, Money.Zero(), "12")); // too short
        Assert.Equal("CARD_REFERENCE_REQUIRED", ex2.ErrorCode);
    }

    [Fact]
    public void PaymentRecord_Card_TenderedMismatch_ThrowsDomainException()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(25000);
        var differentTendered = Money.FromMinorUnits(30000);

        var ex = Assert.Throws<DomainException>(() =>
            new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Card, amount, differentTendered, Money.Zero(), "TXN-987654321"));
        Assert.Equal("CARD_TENDERED_MUST_MATCH_AMOUNT", ex.ErrorCode);
    }

    [Fact]
    public void PaymentRecord_Card_ChangeAmountNonZero_ThrowsDomainException()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(25000);
        var nonZeroChange = Money.FromMinorUnits(100);

        var ex = Assert.Throws<DomainException>(() =>
            new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Card, amount, amount, nonZeroChange, "TXN-987654321"));
        Assert.Equal("CARD_CANNOT_HAVE_CHANGE", ex.ErrorCode);
    }

    [Fact]
    public void PaymentRecord_UPI_ValidInvariants()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(12500);
        var payment = new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.UPI, amount, amount, Money.Zero(), "UPI-REF-123456");

        Assert.Equal(PaymentMethod.UPI, payment.Method);
        Assert.Equal(amount, payment.Amount);
        Assert.Equal(amount, payment.TenderedAmount);
        Assert.Equal(Money.Zero(), payment.ChangeAmount);
        Assert.Equal("UPI-REF-123456", payment.ReferenceNumber);
    }

    [Fact]
    public void PaymentRecord_UPI_InvalidReference_ThrowsDomainException()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(12500);

        var ex = Assert.Throws<DomainException>(() =>
            new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.UPI, amount, amount, Money.Zero(), "123")); // too short for UPI (< 6)
        Assert.Equal("UPI_REFERENCE_REQUIRED", ex.ErrorCode);
    }

    [Fact]
    public void PaymentRecord_DirectMixedMethod_ThrowsDomainException()
    {
        var saleId = Guid.NewGuid();
        var amount = Money.FromMinorUnits(10000);

        var ex = Assert.Throws<DomainException>(() =>
            new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Mixed, amount, amount, Money.Zero(), "MIXED"));
        Assert.Equal("MIXED_PAYMENT_PERSISTENCE_INVALID", ex.ErrorCode);
    }

    [Fact]
    public void SaleInvoice_RefundStateTransitions()
    {
        var invoice = new SaleInvoice(
            Guid.NewGuid(),
            "INV-20260914-000001",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            PricingModel.TaxInclusive,
            Money.FromMinorUnits(10000),
            Money.Zero(),
            Money.Zero(),
            Money.FromMinorUnits(9000),
            Money.FromMinorUnits(1000),
            Money.Zero(),
            Money.FromMinorUnits(10000));

        Assert.Equal(SaleStatus.Completed, invoice.Status);

        invoice.MarkPartiallyRefunded();
        Assert.Equal(SaleStatus.PartiallyRefunded, invoice.Status);

        invoice.MarkFullyRefunded();
        Assert.Equal(SaleStatus.Refunded, invoice.Status);
    }
}
