using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public sealed class Phase8DomainTests
{
    [Theory]
    [InlineData("+91 98765-43210", "9876543210")]
    [InlineData("+919876543210", "9876543210")]
    [InlineData("09876543210", "9876543210")]
    [InlineData("9876543210", "9876543210")]
    [InlineData("98765 43210", "9876543210")]
    public void Customer_PhoneNormalization_ExtractsTenDigits(string input, string expected)
    {
        var customer = new Customer(Guid.NewGuid(), "Ramesh Kumar", input);
        Assert.Equal(expected, customer.Phone);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("5876543210")]
    [InlineData("abc9876543210")]
    [InlineData("987654321")]
    [InlineData("98765432100")]
    public void Customer_InvalidPhone_ThrowsDomainException(string invalidPhone)
    {
        var ex = Assert.Throws<DomainException>(() => new Customer(Guid.NewGuid(), "Ramesh Kumar", invalidPhone));
        Assert.Equal("INVALID_PHONE_NUMBER", ex.ErrorCode);
    }

    [Fact]
    public void Customer_ValidGstin_IsNormalized()
    {
        var customer = new Customer(Guid.NewGuid(), "Ramesh Kumar", "9876543210", gstin: " 29aaaaa0000a1z5 ");
        Assert.Equal("29AAAAA0000A1Z5", customer.Gstin);
    }

    [Fact]
    public void Customer_InvalidGstin_ThrowsDomainException()
    {
        var ex = Assert.Throws<DomainException>(() => new Customer(Guid.NewGuid(), "Ramesh Kumar", "9876543210", gstin: "INVALIDGSTIN"));
        Assert.Equal("INVALID_GSTIN", ex.ErrorCode);
    }

    [Fact]
    public void Customer_NegativeBalanceOperations_ThrowDomainException()
    {
        var customer = new Customer(Guid.NewGuid(), "Ramesh Kumar", "9876543210");
        Assert.Throws<DomainException>(() => customer.AddCredit(Money.FromMinorUnits(-100, Currency.INR)));
        Assert.Throws<DomainException>(() => customer.RecordRepayment(Money.FromMinorUnits(-100, Currency.INR)));
        Assert.Throws<DomainException>(() => customer.IncreaseCreditBalance(Money.FromMinorUnits(-100, Currency.INR)));
        Assert.Throws<DomainException>(() => customer.DecreaseCreditBalance(Money.FromMinorUnits(-100, Currency.INR)));
    }

    [Fact]
    public void Customer_DecreaseCreditBalance_ThrowsWhenInsufficient()
    {
        var customer = new Customer(Guid.NewGuid(), "Ramesh Kumar", "9876543210");
        customer.IncreaseCreditBalance(Money.FromMinorUnits(300, Currency.INR));

        var ex = Assert.Throws<DomainException>(() => customer.DecreaseCreditBalance(Money.FromMinorUnits(400, Currency.INR)));
        Assert.Equal("INSUFFICIENT_CREDIT_BALANCE", ex.ErrorCode);
    }

    [Fact]
    public void Customer_CreditLimit_HardStop_Enforced()
    {
        var customer = new Customer(Guid.NewGuid(), "Ramesh Kumar", "9876543210", creditLimit: 100m);
        customer.AddCredit(Money.FromMinorUnits(8000, Currency.INR));

        // Available credit = 2000 paise
        customer.AddCredit(Money.FromMinorUnits(2000, Currency.INR));
        Assert.Equal(10000, customer.OutstandingCredit.MinorUnits);

        var ex = Assert.Throws<DomainException>(() => customer.AddCredit(Money.FromMinorUnits(1, Currency.INR)));
        Assert.Equal("CREDIT_LIMIT_EXCEEDED", ex.ErrorCode);
    }

    [Fact]
    public void Customer_Repayment_OverpaymentAllocatedToCreditBalance()
    {
        var customer = new Customer(Guid.NewGuid(), "Ramesh Kumar", "9876543210", creditLimit: 100m);
        customer.AddCredit(Money.FromMinorUnits(3000, Currency.INR));
        Assert.Equal(3000, customer.OutstandingCredit.MinorUnits);
        Assert.Equal(0, customer.CreditBalance.MinorUnits);

        // Repay 5000: 3000 clears debt, 2000 becomes Store Credit
        customer.RecordRepayment(Money.FromMinorUnits(5000, Currency.INR));

        Assert.Equal(0, customer.OutstandingCredit.MinorUnits);
        Assert.Equal(2000, customer.CreditBalance.MinorUnits);
    }

    [Fact]
    public void CashSession_ExpectedCashFormula_IncludesCashDropCorrectly()
    {
        var session = new CashSession(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Money.FromMinorUnits(100000, Currency.INR)); // Float ₹1000
        Assert.Equal(100000, session.ExpectedCash.MinorUnits);

        session.AddCashSale(Money.FromMinorUnits(250000, Currency.INR));     // +₹2500
        session.AddCashRepayment(Money.FromMinorUnits(50000, Currency.INR));  // +₹500
        session.AddCashRefund(Money.FromMinorUnits(20000, Currency.INR));     // -₹200

        var payIn = new CashSessionTransaction(0, session.Id, CashTransactionType.PayIn, Money.FromMinorUnits(50000, Currency.INR), "Float topup", Guid.NewGuid(), Guid.NewGuid());
        session.AddPayIn(payIn.Amount); // +₹500

        var payOut = new CashSessionTransaction(0, session.Id, CashTransactionType.PayOut, Money.FromMinorUnits(30000, Currency.INR), "Tea & snacks", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        session.AddPayOut(payOut.Amount); // -₹300

        var cashDrop = new CashSessionTransaction(0, session.Id, CashTransactionType.CashDrop, Money.FromMinorUnits(150000, Currency.INR), "Safe drop", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        session.AddCashDrop(cashDrop.Amount); // -₹1500

        // Formula: 1000 + 2500 + 500 + 500 - 200 - 300 - 1500 = ₹2500 = 250,000 paise
        Assert.Equal(250000, session.ExpectedCash.MinorUnits);
        Assert.Equal(150000, session.TotalCashDrops.MinorUnits);
        Assert.Equal(30000, session.TotalPayOuts.MinorUnits);
        Assert.Equal(50000, session.TotalPayIns.MinorUnits);
        Assert.Equal(20000, session.TotalCashRefunds.MinorUnits);
        Assert.Equal(50000, session.TotalCashRepayments.MinorUnits);
        Assert.Equal(250000, session.TotalCashSales.MinorUnits);
    }

    [Fact]
    public void CashSession_Discrepancy_CalculatedOnClose()
    {
        var session = new CashSession(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Money.FromMinorUnits(100000, Currency.INR));
        session.AddCashSale(Money.FromMinorUnits(200000, Currency.INR));
        // Expected = 300,000 paise (₹3000)

        // Counted = 295,000 paise (₹2950) -> Discrepancy = -5,000 paise (-₹50)
        session.Close(Money.FromMinorUnits(295000, Currency.INR), "Slightly short", Guid.NewGuid());

        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.NotNull(session.Discrepancy);
        Assert.Equal(-5000, session.Discrepancy!.Value.MinorUnits);
        Assert.Equal(295000, session.ClosingCash!.Value.MinorUnits);
    }

    [Fact]
    public void CashSession_CannotAddTransactionOrCloseTwice_Throws()
    {
        var session = new CashSession(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Money.FromMinorUnits(100000, Currency.INR));
        session.Close(Money.FromMinorUnits(100000, Currency.INR), null, Guid.NewGuid());

        var ex1 = Assert.Throws<DomainException>(() => session.Close(Money.FromMinorUnits(100000, Currency.INR), null, Guid.NewGuid()));
        Assert.Equal("SESSION_ALREADY_CLOSED", ex1.ErrorCode);

        var ex2 = Assert.Throws<DomainException>(() => session.AddPayIn(Money.FromMinorUnits(100, Currency.INR)));
        Assert.Equal("SESSION_ALREADY_CLOSED", ex2.ErrorCode);
    }

    [Fact]
    public void CashSession_ForceClose_SetsZeroDiscrepancyAndClosedStatus()
    {
        var session = new CashSession(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Money.FromMinorUnits(50000, Currency.INR));
        session.AddCashSale(Money.FromMinorUnits(100000, Currency.INR));
        // Expected = 150,000 paise

        session.ForceClose(session.ExpectedCash, "Terminal abandoned by cashier for several days", Guid.NewGuid());

        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.True(session.IsForceClosed);
        Assert.Equal(150000, session.ClosingCash!.Value.MinorUnits);
        Assert.Equal(0, session.Discrepancy!.Value.MinorUnits);
        Assert.Equal("Terminal abandoned by cashier for several days", session.ForceCloseReason);
    }
}
