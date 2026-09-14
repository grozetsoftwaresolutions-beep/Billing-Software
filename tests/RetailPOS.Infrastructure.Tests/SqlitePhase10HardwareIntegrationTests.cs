using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Hardware;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Hardware;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Hardware;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class SqlitePhase10HardwareIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqlitePhase10HardwareIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpos_phase10_test_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using var context = new RetailDbContext(_options);
        context.Database.EnsureCreated();
        DatabaseInitializer.InitializeDatabaseAsync(context).GetAwaiter().GetResult();
        SqliteConnectionConfigurator.ApplyPragmas(context);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task DatabaseMigrationHardware_SelfHealingSchemaAndIntegrityCheckPasses()
    {
        using var context = new RetailDbContext(_options);

        // 1. Verify PRAGMA foreign_key_check passes with zero violations
        var conn = (SqliteConnection)context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using (var fkCmd = conn.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_key_check;";
            using var reader = await fkCmd.ExecuteReaderAsync();
            Assert.False(reader.HasRows, "PRAGMA foreign_key_check returned violations!");
        }

        // 2. Verify PRAGMA integrity_check passes
        using (var intCmd = conn.CreateCommand())
        {
            intCmd.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await intCmd.ExecuteScalarAsync();
            Assert.Equal("ok", result);
        }

        // 3. Verify columns exist on sales_invoices
        using (var colCmd = conn.CreateCommand())
        {
            colCmd.CommandText = "PRAGMA table_info(sales_invoices);";
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var r = await colCmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                cols.Add(r.GetString(1));
            }

            Assert.Contains("ReprintCount", cols);
            Assert.Contains("LastReprintedAtUtc", cols);
        }

        // 4. Verify terminal_hardware_configs table exists
        using (var tblCmd = conn.CreateCommand())
        {
            tblCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='terminal_hardware_configs';";
            var tableName = (string?)await tblCmd.ExecuteScalarAsync();
            Assert.Equal("terminal_hardware_configs", tableName);
        }
    }

    [Fact]
    public async Task ReceiptResilience_DatabaseCommitsEvenWhenPrinterFails_AndReprintSucceedsLater()
    {
        using var context = new RetailDbContext(_options);
        var uow = new EfUnitOfWork(context);
        var saleRepo = new EfSaleRepository(context);
        var storeRepo = new EfStoreRepository(context);
        var termRepo = new EfTerminalRepository(context);
        var hwRepo = new TerminalHardwareConfigRepository(context);
        var auditRepo = new EfAuditLogRepository(context);

        // Setup terminal (store is seeded by DatabaseInitializer)
        var terminal = new Terminal(Guid.NewGuid(), "POS-T1", "Counter 1");
        await termRepo.AddAsync(terminal);
        await uow.SaveChangesAsync();

        // 1. Commit financial sale to database first (Authoritative DB commit)
        var invoice = new SaleInvoice(
            Guid.NewGuid(), "INV-RESILIENT-001", Guid.NewGuid(), terminal.Id, Guid.NewGuid(),
            PricingModel.TaxInclusive, Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(),
            Money.FromMinorUnits(10000), Money.Zero(), Money.Zero(), Money.FromMinorUnits(10000));

        invoice.AddPayment(new PaymentRecord(
            Guid.NewGuid(), invoice.Id, PaymentMethod.Cash, Money.FromMinorUnits(10000), Money.FromMinorUnits(10000), Money.Zero()));

        await saleRepo.AddAsync(invoice);
        await uow.SaveChangesAsync();

        // Verify sale is 100% committed to SQLite
        var committedInvoice = await saleRepo.GetByIdAsync(invoice.Id);
        Assert.NotNull(committedInvoice);
        Assert.Equal("INV-RESILIENT-001", committedInvoice.InvoiceNumber);
        Assert.Equal(0, committedInvoice.ReprintCount);
        Assert.Null(committedInvoice.LastReprintedAtUtc);

        // 2. Simulate hardware print failure via a failing printer adapter
        var failingPrinter = new FailingReceiptPrinter();
        var serviceWithFailingPrinter = new ReceiptService(saleRepo, storeRepo, hwRepo, auditRepo, uow, failingPrinter);

        var directPrintResult = await serviceWithFailingPrinter.PrintDirectSaleReceiptAsync(invoice.Id, terminal.Id, Guid.NewGuid(), isReprint: false);
        Assert.Equal(PrintStatus.Failed, directPrintResult.Status);
        Assert.Equal("Printer paper jammed or offline", directPrintResult.ErrorMessage);

        // Verify financial record remains 100% intact and uncorrupted!
        var afterFailedPrint = await saleRepo.GetByIdAsync(invoice.Id);
        Assert.NotNull(afterFailedPrint);
        Assert.Equal(0, afterFailedPrint.ReprintCount);
        Assert.Null(afterFailedPrint.LastReprintedAtUtc);

        // 3. Printer recovers -> execute reprint
        var workingPrinter = new SimulatedReceiptPrinter();
        var serviceWithWorkingPrinter = new ReceiptService(saleRepo, storeRepo, hwRepo, auditRepo, uow, workingPrinter);

        var reprintResult = await serviceWithWorkingPrinter.PrintDirectSaleReceiptAsync(invoice.Id, terminal.Id, Guid.NewGuid(), isReprint: true);
        Assert.Equal(PrintStatus.Success, reprintResult.Status);
        Assert.True(reprintResult.IsReprint);
        Assert.Equal(1, reprintResult.ReprintCount);

        // Verify SQLite reflects atomic counter increment and timestamp
        var reloaded = await saleRepo.GetByIdAsync(invoice.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(1, reloaded.ReprintCount);
        Assert.NotNull(reloaded.LastReprintedAtUtc);

        // Verify audit log has RECEIPT_REPRINTED in SQLite
        var logs = await context.AuditLogs.Where(l => l.Action == "RECEIPT_REPRINTED").ToListAsync();
        Assert.Contains(logs, l => l.Action == "RECEIPT_REPRINTED");
    }

    [Fact]
    public async Task TerminalHardwareConfigRepository_PersistsTerminalScopedConfigurationsIsolated()
    {
        using var context = new RetailDbContext(_options);
        var uow = new EfUnitOfWork(context);
        var termRepo = new EfTerminalRepository(context);
        var hwRepo = new TerminalHardwareConfigRepository(context);

        var term1 = new Terminal(Guid.NewGuid(), "POS-T1", "Counter 1");
        var term2 = new Terminal(Guid.NewGuid(), "POS-T2", "Counter 2");
        await termRepo.AddAsync(term1);
        await termRepo.AddAsync(term2);
        await uow.SaveChangesAsync();

        // Terminal 1: 80mm, Spooler, Drawer Pin 2
        var config1 = new TerminalHardwareConfig(term1.Id);
        config1.UpdatePrinter(PrinterConnectionType.WindowsSpooler, "EPSON TM-T88VI", PaperWidth.Width80mm, PrinterEncoding.Ascii, 0, 5000, true, true);
        config1.UpdateDrawer(DrawerConnectionType.PrinterDriven, 0, 50, 500);
        await hwRepo.AddAsync(config1);

        // Terminal 2: 58mm, Network, Drawer Pin 5
        var config2 = new TerminalHardwareConfig(term2.Id);
        config2.UpdatePrinter(PrinterConnectionType.NetworkTcp, "192.168.1.150", PaperWidth.Width58mm, PrinterEncoding.Windows1252, 16, 3000, true, true);
        config2.UpdateDrawer(DrawerConnectionType.PrinterDriven, 1, 60, 400);
        await hwRepo.AddAsync(config2);

        await uow.SaveChangesAsync();

        // Verify isolated reads
        var loaded1 = await hwRepo.GetByTerminalIdAsync(term1.Id);
        Assert.NotNull(loaded1);
        Assert.Equal(PaperWidth.Width80mm, loaded1.PaperWidth);
        Assert.Equal("EPSON TM-T88VI", loaded1.PrinterNameOrAddress);
        Assert.Equal(0, loaded1.DrawerKickPin);

        var loaded2 = await hwRepo.GetByTerminalIdAsync(term2.Id);
        Assert.NotNull(loaded2);
        Assert.Equal(PaperWidth.Width58mm, loaded2.PaperWidth);
        Assert.Equal("192.168.1.150", loaded2.PrinterNameOrAddress);
        Assert.Equal(1, loaded2.DrawerKickPin);
    }

    [Fact]
    public async Task HardwareTimeoutAndCancellation_CancelsGracefullyWithoutHanging()
    {
        var printer = new SimulatedReceiptPrinter();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await printer.PrintRawBytesAsync(new byte[] { 0x1B, 0x40 }, cts.Token);
        });
    }

    private sealed class FailingReceiptPrinter : IReceiptPrinter
    {
        public Task<PrintResult> PrintRawBytesAsync(byte[] bytes, CancellationToken cancellationToken = default) =>
            Task.FromResult(PrintResult.Fail("Printer paper jammed or offline"));

        public Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceStatusResult.Offline("Printer offline"));
    }
}
