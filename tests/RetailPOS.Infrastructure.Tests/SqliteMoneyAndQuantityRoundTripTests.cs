using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class SqliteMoneyAndQuantityRoundTripTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqliteMoneyAndQuantityRoundTripTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_pos_roundtrip_{Guid.NewGuid():N}.db");
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(12345)]
    [InlineData(10000000000L)]
    public async Task Money_RoundTrip_ShouldPreserveExactIntegerMinorUnits(long minorUnits)
    {
        var productId = Guid.NewGuid();
        var money = Money.FromMinorUnits(minorUnits);

        // 1. Write to real SQLite DB
        using (var writeContext = new RetailDbContext(_options))
        {
            var product = new Product(
                productId,
                $"SKU-{minorUnits}",
                $"Product {minorUnits}",
                UnitOfMeasure.Piece,
                costPrice: money,
                sellingPrice: money,
                taxCategoryId: 1);

            writeContext.Products.Add(product);
            await writeContext.SaveChangesAsync();
        }

        // 2. Read back from SQLite DB in separate context
        using (var readContext = new RetailDbContext(_options))
        {
            var fetched = await readContext.Products.FirstAsync(p => p.Id == productId);

            Assert.Equal(minorUnits, fetched.CostPrice.MinorUnits);
            Assert.Equal(minorUnits, fetched.SellingPrice.MinorUnits);
            Assert.Equal(money.AmountInMajorUnits, fetched.SellingPrice.AmountInMajorUnits);
        }
    }

    [Theory]
    [InlineData("1", "PC")]
    [InlineData("0.005", "KG")]
    [InlineData("1.234", "KG")]
    [InlineData("1.500", "L")]
    [InlineData("100000", "PC")]
    public async Task Quantity_RoundTrip_ShouldPreserveExactDecimalValueAndUnit(string valueStr, string unitCode)
    {
        var productId = Guid.NewGuid();
        var unit = UnitOfMeasure.FromCode(unitCode);
        var quantity = Quantity.From(decimal.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture), unit);

        // 1. Write to SQLite
        using (var writeContext = new RetailDbContext(_options))
        {
            var stock = new StockBalance(productId, unit, initialQuantity: quantity.Value);
            writeContext.StockBalances.Add(stock);
            await writeContext.SaveChangesAsync();
        }

        // 2. Read back in fresh context
        using (var readContext = new RetailDbContext(_options))
        {
            var fetched = await readContext.StockBalances.FirstAsync(s => s.ProductId == productId);

            Assert.Equal(quantity.Value, fetched.CurrentQuantity.Value);
            Assert.Equal(quantity.Unit.Code, fetched.CurrentQuantity.Unit.Code);
            Assert.Equal(quantity.Unit.DecimalPlaces, fetched.CurrentQuantity.Unit.DecimalPlaces);
        }
    }
}

