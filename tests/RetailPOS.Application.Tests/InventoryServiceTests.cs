using RetailPOS.Application.Services;
using RetailPOS.Application.Tests.Fakes;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Application.Tests;

public class InventoryServiceTests
{
    private readonly FakeProductRepository _productRepo = new();
    private readonly FakeInventoryRepository _inventoryRepo = new();
    private readonly FakeAuditLogRepository _auditRepo = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly InventoryService _service;
    private readonly Product _testProduct;

    public InventoryServiceTests()
    {
        _service = new InventoryService(
            _productRepo,
            _inventoryRepo,
            _auditRepo,
            _uow);

        _testProduct = new Product(
            Guid.NewGuid(),
            "INV-PROD",
            "Inventory Test Product",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);
        _productRepo.AddAsync(_testProduct).GetAwaiter().GetResult();

        var initialBalance = new StockBalance(_testProduct.Id, UnitOfMeasure.Piece, initialQuantity: 10m, reorderLevel: 5m);
        _inventoryRepo.AddStockBalanceAsync(initialBalance).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task AdjustStock_Increase_ShouldUpdateBalanceAndRecordMovement()
    {
        var request = new StockAdjustmentRequest(
            _testProduct.Id,
            ManualStockAdjustmentType.Increase,
            Quantity: 5m,
            Reason: "Found extra inventory on shelf");

        var response = await _service.AdjustStockAsync(request, null, null, "manager");

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal(15m, response.Data.ResultingQuantity);
        Assert.Equal(StockMovementType.AdjustmentIncrease, response.Data.MovementType);

        var balance = await _inventoryRepo.GetStockBalanceAsync(_testProduct.Id);
        Assert.NotNull(balance);
        Assert.Equal(15m, balance.CurrentQuantity.Value);

        Assert.Contains(_auditRepo.Logs, l => l.Action == "STOCK_ADJUSTED");
    }

    [Fact]
    public async Task AdjustStock_Decrease_ShouldUpdateBalanceAndRecordMovement()
    {
        var request = new StockAdjustmentRequest(
            _testProduct.Id,
            ManualStockAdjustmentType.Decrease,
            Quantity: 3m,
            Reason: "Damaged packaging discarded");

        var response = await _service.AdjustStockAsync(request, null, null, "manager");

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal(7m, response.Data.ResultingQuantity);
        Assert.Equal(StockMovementType.AdjustmentDecrease, response.Data.MovementType);

        var balance = await _inventoryRepo.GetStockBalanceAsync(_testProduct.Id);
        Assert.NotNull(balance);
        Assert.Equal(7m, balance.CurrentQuantity.Value);
    }

    [Fact]
    public async Task AdjustStock_DecreaseExceedingAvailable_ShouldThrowInsufficientStockException()
    {
        var request = new StockAdjustmentRequest(
            _testProduct.Id,
            ManualStockAdjustmentType.Decrease,
            Quantity: 15m, // Current is 10
            Reason: "Attempt to decrease more than stock");

        var ex = await Assert.ThrowsAsync<InsufficientStockException>(() =>
            _service.AdjustStockAsync(request, null, null, "manager"));

        Assert.Equal("INSUFFICIENT_STOCK", ex.ErrorCode);
    }

    [Fact]
    public async Task AdjustStock_ZeroOrNegativeQuantity_ShouldThrowDomainException()
    {
        var zeroReq = new StockAdjustmentRequest(
            _testProduct.Id,
            ManualStockAdjustmentType.Increase,
            Quantity: 0m,
            Reason: "Zero quantity test");

        var ex1 = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AdjustStockAsync(zeroReq, null, null, "manager"));
        Assert.Equal("INVALID_ADJUSTMENT_QUANTITY", ex1.ErrorCode);

        var negReq = new StockAdjustmentRequest(
            _testProduct.Id,
            ManualStockAdjustmentType.Increase,
            Quantity: -5m,
            Reason: "Negative quantity test");

        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AdjustStockAsync(negReq, null, null, "manager"));
        Assert.Equal("INVALID_ADJUSTMENT_QUANTITY", ex2.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AdjustStock_EmptyOrWhitespaceReason_ShouldThrowDomainException(string reason)
    {
        var request = new StockAdjustmentRequest(
            _testProduct.Id,
            ManualStockAdjustmentType.Increase,
            Quantity: 5m,
            Reason: reason);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AdjustStockAsync(request, null, null, "manager"));
        Assert.Equal("REASON_REQUIRED", ex.ErrorCode);
    }

    [Fact]
    public async Task AdjustStock_ReasonTooLong_ShouldThrowDomainException()
    {
        var request = new StockAdjustmentRequest(
            _testProduct.Id,
            ManualStockAdjustmentType.Increase,
            Quantity: 5m,
            Reason: new string('X', 251));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AdjustStockAsync(request, null, null, "manager"));
        Assert.Equal("REASON_TOO_LONG", ex.ErrorCode);
    }

    [Fact]
    public async Task AdjustStock_InvalidMovementType_ShouldThrowDomainException()
    {
        var request = new StockAdjustmentRequest(
            _testProduct.Id,
            (ManualStockAdjustmentType)999,
            Quantity: 5m,
            Reason: "Invalid type injection attempt");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AdjustStockAsync(request, null, null, "manager"));
        Assert.Equal("INVALID_ADJUSTMENT_TYPE", ex.ErrorCode);
    }
}