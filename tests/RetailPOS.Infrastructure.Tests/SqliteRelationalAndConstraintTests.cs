using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class SqliteRelationalAndConstraintTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqliteRelationalAndConstraintTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_pos_constraints_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using var context = new RetailDbContext(_options);
        context.Database.EnsureCreated();
        SqliteConnectionConfigurator.ApplyPragmas(context);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task DuplicateSKU_ShouldThrowDbUpdateException()
    {
        using var context = new RetailDbContext(_options);

        var p1 = new Product(Guid.NewGuid(), "DUPLICATE-SKU", "Prod 1", UnitOfMeasure.Piece, Money.Zero(), Money.Zero(), 1);
        var p2 = new Product(Guid.NewGuid(), "DUPLICATE-SKU", "Prod 2", UnitOfMeasure.Piece, Money.Zero(), Money.Zero(), 1);

        context.Products.Add(p1);
        await context.SaveChangesAsync();

        context.Products.Add(p2);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateBarcode_ShouldThrowDbUpdateException()
    {
        using var context = new RetailDbContext(_options);

        var p1 = new Product(Guid.NewGuid(), "SKU-A", "Prod A", UnitOfMeasure.Piece, Money.Zero(), Money.Zero(), 1);
        var p2 = new Product(Guid.NewGuid(), "SKU-B", "Prod B", UnitOfMeasure.Piece, Money.Zero(), Money.Zero(), 1);

        var barcode = new Barcode("8901234567890");
        p1.AddBarcode(barcode, true);
        p2.AddBarcode(barcode, true);

        context.Products.AddRange(p1, p2);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateClientTransactionId_ShouldBeBlockedByIdempotencyConstraint()
    {
        using var context = new RetailDbContext(_options);
        var duplicateClientTxId = Guid.NewGuid();

        var sale1 = new SaleInvoice(
            Guid.NewGuid(), "INV-001", duplicateClientTxId, Guid.NewGuid(), Guid.NewGuid(),
            PricingModel.TaxExclusive, Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero());

        var sale2 = new SaleInvoice(
            Guid.NewGuid(), "INV-002", duplicateClientTxId, Guid.NewGuid(), Guid.NewGuid(),
            PricingModel.TaxExclusive, Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero());

        context.SalesInvoices.Add(sale1);
        await context.SaveChangesAsync();

        context.SalesInvoices.Add(sale2);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task InvoiceSequence_ShouldGenerateMonotonicUniqueNumbers()
    {
        using var context = new RetailDbContext(_options);
        var generator = new InvoiceSequenceGenerator(context);

        var inv1 = await generator.GenerateNextInvoiceNumberAsync();
        var inv2 = await generator.GenerateNextInvoiceNumberAsync();
        var inv3 = await generator.GenerateNextInvoiceNumberAsync();

        Assert.NotEqual(inv1, inv2);
        Assert.NotEqual(inv2, inv3);
        Assert.EndsWith("000001", inv1);
        Assert.EndsWith("000002", inv2);
        Assert.EndsWith("000003", inv3);
    }
}

