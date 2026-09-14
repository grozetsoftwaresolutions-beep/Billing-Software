using System.Text;
using RetailPOS.Application.Hardware;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Application.Tests.Fakes;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Hardware;
using Xunit;

namespace RetailPOS.Application.Tests;

public sealed class Phase10HardwareApplicationTests
{
    private sealed class FakeSaleRepository : ISaleRepository
    {
        public List<SaleInvoice> Invoices { get; } = [];

        public Task<SaleInvoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Invoices.FirstOrDefault(i => i.Id == id));

        public Task<SaleInvoice?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(Invoices.FirstOrDefault(i => i.InvoiceNumber == invoiceNumber));

        public Task<SaleInvoice?> GetByClientTransactionIdAsync(Guid clientTransactionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Invoices.FirstOrDefault(i => i.ClientTransactionId == clientTransactionId));

        public Task<bool> ExistsByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(Invoices.Any(i => i.InvoiceNumber == invoiceNumber));

        public Task<(IReadOnlyList<SaleInvoice> Items, int TotalCount)> SearchAsync(SaleSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<SaleInvoice> Items, int TotalCount)>((Invoices, Invoices.Count));

        public Task AddAsync(SaleInvoice invoice, CancellationToken cancellationToken = default)
        {
            Invoices.Add(invoice);
            return Task.CompletedTask;
        }

        public Task<int> IncrementReprintCountAsync(Guid saleInvoiceId, DateTime reprintedAtUtc, CancellationToken cancellationToken = default)
        {
            var inv = Invoices.FirstOrDefault(i => i.Id == saleInvoiceId);
            if (inv != null)
            {
                inv.RecordReprint();
            }
            return Task.FromResult(1);
        }

        public Task UpdateAsync(SaleInvoice invoice, CancellationToken cancellationToken = default)
        {
            var idx = Invoices.FindIndex(i => i.Id == invoice.Id);
            if (idx >= 0) Invoices[idx] = invoice;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void CashDrawerPolicy_EvaluatesEligibleAndForbiddenTransactionsCorrectly()
    {
        var drawer = new SimulatedCashDrawer();
        var audit = new FakeAuditLogRepository();
        var uow = new FakeUnitOfWork();
        var service = new CashDrawerService(drawer, audit, uow);

        // 1. Cash Sale -> YES
        var cashSale = new SaleInvoice(
            Guid.NewGuid(), "INV-001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PricingModel.TaxInclusive, Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(),
            Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(10000));

        cashSale.AddPayment(new PaymentRecord(
            Guid.NewGuid(), cashSale.Id, PaymentMethod.Cash, Money.FromMinorUnits(10000), Money.FromMinorUnits(10000), Money.Zero()));

        var cashResult = service.EvaluateSaleDrawerKick(cashSale);
        Assert.True(cashResult.ShouldKick);
        Assert.Equal("CASH_SALE", cashResult.Reason);
        Assert.Equal(10000, cashResult.AmountPaise);

        // 2. Mixed Sale (Cash + Card) -> YES
        var mixedSale = new SaleInvoice(
            Guid.NewGuid(), "INV-002", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PricingModel.TaxInclusive, Money.FromMinorUnits(20000), Money.Zero(), Money.Zero(),
            Money.FromMinorUnits(20000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(20000));

        mixedSale.AddPayment(new PaymentRecord(
            Guid.NewGuid(), mixedSale.Id, PaymentMethod.Card, Money.FromMinorUnits(15000), Money.FromMinorUnits(15000), Money.Zero(), "CARD-1234"));
        mixedSale.AddPayment(new PaymentRecord(
            Guid.NewGuid(), mixedSale.Id, PaymentMethod.Cash, Money.FromMinorUnits(5000), Money.FromMinorUnits(5000), Money.Zero()));

        var mixedResult = service.EvaluateSaleDrawerKick(mixedSale);
        Assert.True(mixedResult.ShouldKick);
        Assert.Equal("MIXED_CASH_SALE", mixedResult.Reason);
        Assert.Equal(5000, mixedResult.AmountPaise);

        // 3. Pure Card Sale -> NO
        var cardSale = new SaleInvoice(
            Guid.NewGuid(), "INV-003", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PricingModel.TaxInclusive, Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(),
            Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(10000));

        cardSale.AddPayment(new PaymentRecord(
            Guid.NewGuid(), cardSale.Id, PaymentMethod.Card, Money.FromMinorUnits(10000), Money.FromMinorUnits(10000), Money.Zero(), "CARD-5678"));

        var cardResult = service.EvaluateSaleDrawerKick(cardSale);
        Assert.False(cardResult.ShouldKick);

        // 4. Pure UPI Sale -> NO
        var upiSale = new SaleInvoice(
            Guid.NewGuid(), "INV-004", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PricingModel.TaxInclusive, Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(),
            Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(10000));

        upiSale.AddPayment(new PaymentRecord(
            Guid.NewGuid(), upiSale.Id, PaymentMethod.UPI, Money.FromMinorUnits(10000), Money.FromMinorUnits(10000), Money.Zero(), "UPI-9999"));

        var upiResult = service.EvaluateSaleDrawerKick(upiSale);
        Assert.False(upiResult.ShouldKick);

        // 5. Cash Refund -> YES
        var cashRefund = new SaleRefund(
            Guid.NewGuid(), cashSale.Id, "REF-001", Guid.NewGuid(), Guid.NewGuid(), "Manager", Guid.NewGuid(),
            PaymentMethod.Cash, Money.FromMinorUnits(5000), "Customer return");

        var refundResult = service.EvaluateRefundDrawerKick(cashRefund);
        Assert.True(refundResult.ShouldKick);
        Assert.Equal("CASH_REFUND", refundResult.Reason);
        Assert.Equal(5000, refundResult.AmountPaise);

        // 6. Card / Non-Cash Refund -> NO
        var cardRefund = new SaleRefund(
            Guid.NewGuid(), cashSale.Id, "REF-002", Guid.NewGuid(), Guid.NewGuid(), "Manager", Guid.NewGuid(),
            PaymentMethod.Card, Money.FromMinorUnits(5000), "Customer return");

        var cardRefundResult = service.EvaluateRefundDrawerKick(cardRefund);
        Assert.False(cardRefundResult.ShouldKick);
    }

    [Fact]
    public void BarcodeScannerParser_ClassifiesBurstAndNormalizesPreamble()
    {
        var parser = new KeyboardWedgeParser(maxAvgIntervalMs: 35, maxSingleIntervalMs: 60);

        // 1. Scanner Burst: 12 characters with 5ms interval each
        var scannerKeystrokes = new List<(char Key, long IntervalMs)>
        {
            (']', 0), ('E', 5), ('0', 4), ('8', 5), ('9', 6), ('0', 4),
            ('1', 5), ('2', 6), ('3', 4), ('4', 5), ('5', 6), ('\r', 5)
        };

        var burstResult = parser.ClassifyInput(scannerKeystrokes);
        Assert.Equal(BarcodeScanSource.ScannerWedge, burstResult.Source);
        Assert.Equal("89012345", burstResult.NormalizedBarcode);
        Assert.True(burstResult.IsValid);

        // 2. Human typing: 5 characters with 150ms interval each
        var manualKeystrokes = new List<(char Key, long IntervalMs)>
        {
            ('8', 120), ('9', 150), ('0', 140), ('1', 180), ('\r', 160)
        };

        var manualResult = parser.ClassifyInput(manualKeystrokes);
        Assert.Equal(BarcodeScanSource.ManualEntry, manualResult.Source);
        Assert.Equal("8901", manualResult.NormalizedBarcode);
        Assert.True(manualResult.IsValid);
    }

    [Fact]
    public void ScaleProtocolParser_ParsesAsciiAndCasFramesCorrectly()
    {
        var asciiParser = new GenericAsciiScaleParser();
        var casParser = new CasToledoScaleParser();

        // 1. Stable gross weight: ST,GS,+   1.250kg\r\n
        var stableBytes = Encoding.ASCII.GetBytes("ST,GS,+   1.250kg\r\n");
        var stableResult = asciiParser.ParseFrame(stableBytes);
        Assert.True(stableResult.Success);
        Assert.Equal(1.250m, stableResult.Weight);
        Assert.Equal(ScaleWeightUnit.Kilogram, stableResult.Unit);
        Assert.True(stableResult.IsStable);
        Assert.False(stableResult.IsTare);

        // 2. Unstable weight: US,GS,+   1.250kg\r\n
        var unstableBytes = Encoding.ASCII.GetBytes("US,GS,+   1.250kg\r\n");
        var unstableResult = asciiParser.ParseFrame(unstableBytes);
        Assert.True(unstableResult.Success);
        Assert.Equal(1.250m, unstableResult.Weight);
        Assert.False(unstableResult.IsStable);

        // 3. Tare / Net weight: ST,NT,+   0.850kg\r\n
        var tareBytes = Encoding.ASCII.GetBytes("ST,NT,+   0.850kg\r\n");
        var tareResult = asciiParser.ParseFrame(tareBytes);
        Assert.True(tareResult.Success);
        Assert.Equal(0.850m, tareResult.Weight);
        Assert.True(tareResult.IsTare);

        // 4. Weight in Grams: ST,GS,+     500g\r\n
        var gramBytes = Encoding.ASCII.GetBytes("ST,GS,+     500g\r\n");
        var gramResult = asciiParser.ParseFrame(gramBytes);
        Assert.True(gramResult.Success);
        Assert.Equal(500m, gramResult.Weight);
        Assert.Equal(ScaleWeightUnit.Gram, gramResult.Unit);

        // 5. CAS / Toledo demand-response frame: 01.250kg\r\n
        var casBytes = Encoding.ASCII.GetBytes("01.250kg\r\n");
        var casResult = casParser.ParseFrame(casBytes);
        Assert.True(casResult.Success);
        Assert.Equal(1.250m, casResult.Weight);

        // 6. Corrupted frame
        var corruptBytes = Encoding.ASCII.GetBytes("CORRUPT_FRAME\r\n");
        var corruptResult = asciiParser.ParseFrame(corruptBytes);
        Assert.False(corruptResult.Success);
    }

    [Fact]
    public async Task ScaleToQuantityIntegration_ConvertsExactDecimalQuantities()
    {
        var scale = new SimulatedWeighingScale();
        var audit = new FakeAuditLogRepository();
        var uow = new FakeUnitOfWork();
        var service = new WeighingScaleService(scale, audit, uow);

        // 1. Kg to Kg direct match: 1.250 kg -> Quantity(1.250, KG)
        var result1 = await service.ValidateAndConvertWeightAsync(1.250m, ScaleWeightUnit.Kilogram, UnitOfMeasure.Kilogram);
        Assert.True(result1.Success);
        Assert.Equal(1.250m, result1.Data!.Value);
        Assert.Equal(UnitOfMeasure.Kilogram, result1.Data!.Unit);

        // 2. Gram to Kg conversion: 500 g -> Quantity(0.500, KG)
        var result2 = await service.ValidateAndConvertWeightAsync(500m, ScaleWeightUnit.Gram, UnitOfMeasure.Kilogram);
        Assert.True(result2.Success);
        Assert.Equal(0.500m, result2.Data!.Value);
        Assert.Equal(UnitOfMeasure.Kilogram, result2.Data!.Unit);

        // 3. Zero weight -> Rejected
        var resultZero = await service.ValidateAndConvertWeightAsync(0m, ScaleWeightUnit.Kilogram, UnitOfMeasure.Kilogram);
        Assert.False(resultZero.Success);
        Assert.Equal("INVALID_WEIGHT", resultZero.ErrorCode);

        // 4. Incompatible UOM (Scale KG to Piece) -> Rejected
        var resultIncompat = await service.ValidateAndConvertWeightAsync(1.000m, ScaleWeightUnit.Kilogram, UnitOfMeasure.Piece);
        Assert.False(resultIncompat.Success);
        Assert.Equal("INCOMPATIBLE_UOM", resultIncompat.ErrorCode);
    }

    [Fact]
    public async Task ReprintAtomicity_UpdatesCountersOnlyAfterPhysicalConfirmation()
    {
        var printer = new SimulatedReceiptPrinter();
        var saleRepo = new FakeSaleRepository();
        var storeRepo = new FakeStoreRepository();
        var configRepo = new FakeTerminalHardwareConfigRepository();
        var audit = new FakeAuditLogRepository();
        var uow = new FakeUnitOfWork();

        var service = new ReceiptService(saleRepo, storeRepo, configRepo, audit, uow, printer);

        var invoice = new SaleInvoice(
            Guid.NewGuid(), "INV-RP-001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PricingModel.TaxInclusive, Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(),
            Money.FromMinorUnits(5000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(5000));

        invoice.AddPayment(new PaymentRecord(
            Guid.NewGuid(), invoice.Id, PaymentMethod.Cash, Money.FromMinorUnits(5000), Money.FromMinorUnits(5000), Money.Zero()));

        await saleRepo.AddAsync(invoice);

        // Step 1: Generate reprint payload -> ReprintCount must remain 0!
        var payload = await service.GenerateSaleReceiptPayloadAsync(invoice.Id, Guid.NewGuid(), isReprint: true);
        Assert.NotNull(payload.RawBytes);
        Assert.Equal(0, invoice.ReprintCount);
        Assert.Null(invoice.LastReprintedAtUtc);

        // Step 2A: Confirmation -> ReprintCount becomes 1, LastReprintedAtUtc set
        var confirmResult = await service.ConfirmReprintAsync(invoice.Id, Guid.NewGuid(), Guid.NewGuid(), "Customer lost receipt");
        Assert.Equal(PrintStatus.Success, confirmResult.Status);
        Assert.Equal(1, invoice.ReprintCount);
        Assert.NotNull(invoice.LastReprintedAtUtc);
        Assert.Contains(audit.Logs, l => l.Action == "RECEIPT_REPRINTED");

        // Step 2B: Failed reprint reported -> ReprintCount remains 1 (does not increment!)
        var failedResult = await service.ReportReprintFailedAsync(invoice.Id, Guid.NewGuid(), Guid.NewGuid(), "Printer out of paper");
        Assert.Equal(PrintStatus.Failed, failedResult.Status);
        Assert.Equal(1, invoice.ReprintCount); // Invariant: unchanged on failure
        Assert.Contains(audit.Logs, l => l.Action == "REPRINT_FAILED");
    }

    private sealed class FakeTerminalHardwareConfigRepository : ITerminalHardwareConfigRepository
    {
        public Task<Domain.Entities.Hardware.TerminalHardwareConfig?> GetByTerminalIdAsync(Guid terminalId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Domain.Entities.Hardware.TerminalHardwareConfig?>(new Domain.Entities.Hardware.TerminalHardwareConfig(terminalId));

        public Task AddAsync(Domain.Entities.Hardware.TerminalHardwareConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Domain.Entities.Hardware.TerminalHardwareConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void CashDrawerEligibility_CustomerRepayment_CashKicks_BankTransferDoesNotKick()
    {
        var drawer = new SimulatedCashDrawer();
        var audit = new FakeAuditRepo();
        var uow = new FakeUnitOfWork();
        var service = new CashDrawerService(drawer, audit, uow);

        // Cash customer repayment -> ShouldKick: true
        var cashDirective = service.EvaluateCustomerPaymentDrawerKick(PaymentMethod.Cash, 5000);
        Assert.True(cashDirective.ShouldKick);
        Assert.Equal("CASH_CUSTOMER_REPAYMENT", cashDirective.Reason);
        Assert.Equal(5000, cashDirective.AmountPaise);

        // BankTransfer repayment -> ShouldKick: false
        var bankDirective = service.EvaluateCustomerPaymentDrawerKick(PaymentMethod.BankTransfer, 5000);
        Assert.False(bankDirective.ShouldKick);
        Assert.Null(bankDirective.Reason);

        // UPI repayment -> ShouldKick: false
        var upiDirective = service.EvaluateCustomerPaymentDrawerKick(PaymentMethod.UPI, 5000);
        Assert.False(upiDirective.ShouldKick);
    }

    [Fact]
    public void CashDrawerEligibility_CashSession_PayIn_PayOut_CashDrop_AllKick()
    {
        var drawer = new SimulatedCashDrawer();
        var audit = new FakeAuditRepo();
        var uow = new FakeUnitOfWork();
        var service = new CashDrawerService(drawer, audit, uow);

        // PayIn -> ShouldKick: true
        var payIn = service.EvaluateCashSessionDrawerKick(CashTransactionType.PayIn, 1000);
        Assert.True(payIn.ShouldKick);
        Assert.Equal("PAYIN", payIn.Reason);
        Assert.Equal(1000, payIn.AmountPaise);

        // PayOut -> ShouldKick: true
        var payOut = service.EvaluateCashSessionDrawerKick(CashTransactionType.PayOut, 2000);
        Assert.True(payOut.ShouldKick);
        Assert.Equal("PAYOUT", payOut.Reason);
        Assert.Equal(2000, payOut.AmountPaise);

        // CashDrop -> ShouldKick: true
        var cashDrop = service.EvaluateCashSessionDrawerKick(CashTransactionType.CashDrop, 3000);
        Assert.True(cashDrop.ShouldKick);
        Assert.Equal("CASHDROP", cashDrop.Reason);
        Assert.Equal(3000, cashDrop.AmountPaise);
    }

    [Fact]
    public async Task CashDrawerService_PhysicalKickFails_EmitsDrawerKickFailed()
    {
        var drawer = new SimulatedCashDrawer { SimulateJammed = true };
        var audit = new FakeAuditRepo();
        var uow = new FakeUnitOfWork();
        var service = new CashDrawerService(drawer, audit, uow);

        var res = await service.TriggerDrawerKickAsync(Guid.NewGuid(), Guid.NewGuid(), "Failed kick test", isManualOpen: true);
        Assert.False(res.Success);

        // Must emit DRAWER_KICK_FAILED, not DRAWER_OPENED_MANUALLY
        Assert.Contains(audit.RecordedActions, a => a.Action == "DRAWER_KICK_FAILED");
        Assert.DoesNotContain(audit.RecordedActions, a => a.Action == "DRAWER_OPENED_MANUALLY");
    }

    [Fact]
    public async Task CashDrawerService_RecordAutomatedDrawerKickAudit_EmitsCorrectSaleRefundFailedActions()
    {
        var drawer = new SimulatedCashDrawer();
        var audit = new FakeAuditRepo();
        var uow = new FakeUnitOfWork();
        var service = new CashDrawerService(drawer, audit, uow);
        var terminalId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // 1. Sale success -> DRAWER_OPENED_SALE
        await service.RecordAutomatedDrawerKickAuditAsync(terminalId, userId, "SALE", Guid.NewGuid(), true, null);
        Assert.Contains(audit.RecordedActions, a => a.Action == "DRAWER_OPENED_SALE");

        // 2. Refund success -> DRAWER_OPENED_REFUND
        await service.RecordAutomatedDrawerKickAuditAsync(terminalId, userId, "REFUND", Guid.NewGuid(), true, null);
        Assert.Contains(audit.RecordedActions, a => a.Action == "DRAWER_OPENED_REFUND");

        // 3. Any failure -> DRAWER_KICK_FAILED
        await service.RecordAutomatedDrawerKickAuditAsync(terminalId, userId, "SALE", Guid.NewGuid(), false, "Hardware timeout");
        Assert.Contains(audit.RecordedActions, a => a.Action == "DRAWER_KICK_FAILED");
    }

    sealed class FakeAuditRepo : IAuditLogRepository
    {
        public List<(string EntityName, string Action, string PerformedBy, string Details)> RecordedActions { get; } = [];
        public Task RecordAsync(string entityName, string action, string performedBy, string details, CancellationToken cancellationToken = default)
        {
            RecordedActions.Add((entityName, action, performedBy, details));
            return Task.CompletedTask;
        }
    }

    sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<IDbTransactionScope> BeginSerializableTransactionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IDbTransactionScope>(new FakeTransactionScope());
        public bool IsTransient(Exception ex) => false;
        public void ResetChangeTracker() { }

        private sealed class FakeTransactionScope : IDbTransactionScope
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

}
