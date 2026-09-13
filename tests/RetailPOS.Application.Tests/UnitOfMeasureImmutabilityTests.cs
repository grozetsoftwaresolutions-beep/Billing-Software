using RetailPOS.Application.Services;
using RetailPOS.Application.Tests.Fakes;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Application.Tests;

public class UnitOfMeasureImmutabilityTests
{
    private readonly FakeProductRepository _productRepo = new();
    private readonly FakeInventoryRepository _inventoryRepo = new();
    private readonly FakeCategoryRepository _categoryRepo = new();
    private readonly FakeAuditLogRepository _auditRepo = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly ProductService _service;

    public UnitOfMeasureImmutabilityTests()
    {
        _service = new ProductService(
            _productRepo,
            _inventoryRepo,
            _categoryRepo,
            _auditRepo,
            _uow);
    }

    [Fact]
    public async Task UpdateProduct_WhenCurrentStockNonZero_ShouldRejectUnitChange()
    {
        var product = new Product(
            Guid.NewGuid(),
            "UNIT-STOCK",
            "Non-zero stock product",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);
        await _productRepo.AddAsync(product);

        // Current stock = 5
        var balance = new StockBalance(product.Id, UnitOfMeasure.Piece, initialQuantity: 5m);
        await _inventoryRepo.AddStockBalanceAsync(balance);

        var updateRequest = new UpdateProductRequest(
            Name: "Updated Name",
            UnitCode: "KG", // Changed unit
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            Notes: null);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.UpdateProductAsync(product.Id, updateRequest, "admin"));

        Assert.Equal("UNIT_IMMUTABLE", ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateProduct_WhenStockMovementExists_ShouldRejectUnitChange()
    {
        var product = new Product(
            Guid.NewGuid(),
            "UNIT-MOV",
            "Movement history product",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);
        await _productRepo.AddAsync(product);

        // Current stock = 0, but movement exists
        var balance = new StockBalance(product.Id, UnitOfMeasure.Piece, initialQuantity: 0m);
        await _inventoryRepo.AddStockBalanceAsync(balance);

        var movement = new StockMovement(
            1,
            product.Id,
            StockMovementType.AdjustmentIncrease,
            Quantity.From(10m, UnitOfMeasure.Piece),
            Quantity.From(10m, UnitOfMeasure.Piece),
            "REF-1",
            "Initial",
            null,
            null);
        await _inventoryRepo.AddStockMovementAsync(movement);

        var updateRequest = new UpdateProductRequest(
            Name: "Updated Name",
            UnitCode: "KG", // Changed unit
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            Notes: null);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.UpdateProductAsync(product.Id, updateRequest, "admin"));

        Assert.Equal("UNIT_IMMUTABLE", ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateProduct_WhenSalesHistoryExists_ShouldRejectUnitChange()
    {
        var product = new Product(
            Guid.NewGuid(),
            "UNIT-SALE",
            "Sales history product",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);
        await _productRepo.AddAsync(product);

        var balance = new StockBalance(product.Id, UnitOfMeasure.Piece, initialQuantity: 0m);
        await _inventoryRepo.AddStockBalanceAsync(balance);

        // Record sales history
        _productRepo.ProductsWithSales.Add(product.Id);

        var updateRequest = new UpdateProductRequest(
            Name: "Updated Name",
            UnitCode: "KG",
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            Notes: null);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.UpdateProductAsync(product.Id, updateRequest, "admin"));

        Assert.Equal("UNIT_IMMUTABLE", ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateProduct_WhenPurchaseHistoryExists_ShouldRejectUnitChange()
    {
        var product = new Product(
            Guid.NewGuid(),
            "UNIT-PURCH",
            "Purchase history product",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);
        await _productRepo.AddAsync(product);

        var balance = new StockBalance(product.Id, UnitOfMeasure.Piece, initialQuantity: 0m);
        await _inventoryRepo.AddStockBalanceAsync(balance);

        // Record purchase history
        _productRepo.ProductsWithPurchases.Add(product.Id);

        var updateRequest = new UpdateProductRequest(
            Name: "Updated Name",
            UnitCode: "KG",
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            Notes: null);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.UpdateProductAsync(product.Id, updateRequest, "admin"));

        Assert.Equal("UNIT_IMMUTABLE", ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateProduct_WhenZeroStockAndNoHistory_ShouldAllowUnitChange()
    {
        var product = new Product(
            Guid.NewGuid(),
            "UNIT-NEW",
            "Brand new product with zero usage",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);
        await _productRepo.AddAsync(product);

        var balance = new StockBalance(product.Id, UnitOfMeasure.Piece, initialQuantity: 0m);
        await _inventoryRepo.AddStockBalanceAsync(balance);

        var updateRequest = new UpdateProductRequest(
            Name: "Updated Name",
            UnitCode: "KG", // Changed unit on fresh zero-usage product
            CostPricePaise: 120,
            SellingPricePaise: 180,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            Notes: "Switched to KG");

        var response = await _service.UpdateProductAsync(product.Id, updateRequest, "admin");

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("KG", response.Data.UnitCode);
        Assert.Equal("KG", product.Unit.Code);
    }
}