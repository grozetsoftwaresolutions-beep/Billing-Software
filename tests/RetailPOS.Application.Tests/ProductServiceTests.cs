using RetailPOS.Application.Services;
using RetailPOS.Application.Tests.Fakes;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Application.Tests;

public class ProductServiceTests
{
    private readonly FakeProductRepository _productRepo = new();
    private readonly FakeInventoryRepository _inventoryRepo = new();
    private readonly FakeCategoryRepository _categoryRepo = new();
    private readonly FakeAuditLogRepository _auditRepo = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly ProductService _service;

    public ProductServiceTests()
    {
        _service = new ProductService(
            _productRepo,
            _inventoryRepo,
            _categoryRepo,
            _auditRepo,
            _uow);
    }

    [Fact]
    public async Task CreateProduct_ValidRequest_ShouldCreateProductAndStockBalance()
    {
        var request = new CreateProductRequest(
            Sku: "TEST-PROD-1",
            Name: "Test Product 1",
            UnitCode: "PC",
            CostPricePaise: 1000,
            SellingPricePaise: 1500,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            PrimaryBarcode: "8901234567890",
            InitialStock: 25m,
            ReorderLevel: 5m,
            Notes: "Initial batch");

        var response = await _service.CreateProductAsync(request, "admin");

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("TEST-PROD-1", response.Data.Sku);
        Assert.Equal(25m, response.Data.CurrentStock);
        Assert.False(response.Data.IsLowStock);

        // Verify stock balance was added
        var balance = await _inventoryRepo.GetStockBalanceAsync(response.Data.Id);
        Assert.NotNull(balance);
        Assert.Equal(25m, balance.CurrentQuantity.Value);

        // Verify movement was created
        Assert.Single(_inventoryRepo.Movements);
        Assert.Equal(25m, _inventoryRepo.Movements[0].QuantityChanged.Value);

        // Verify audit log
        Assert.Contains(_auditRepo.Logs, l => l.Action == "PRODUCT_CREATED");
    }

    [Fact]
    public async Task CreateProduct_DuplicateSku_ShouldThrowDomainException()
    {
        var request1 = new CreateProductRequest(
            Sku: "DUPL-SKU",
            Name: "First",
            UnitCode: "PC",
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            PrimaryBarcode: null,
            InitialStock: 0m,
            ReorderLevel: 0m,
            Notes: null);

        await _service.CreateProductAsync(request1, "admin");

        var request2 = new CreateProductRequest(
            Sku: "dupl-sku", // Lowercase but same normalized SKU
            Name: "Second",
            UnitCode: "PC",
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            PrimaryBarcode: null,
            InitialStock: 0m,
            ReorderLevel: 0m,
            Notes: null);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.CreateProductAsync(request2, "admin"));
        Assert.Equal("SKU_ALREADY_EXISTS", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateProduct_InvalidTaxCategory_ShouldThrowDomainException()
    {
        var request = new CreateProductRequest(
            Sku: "TAX-INVALID",
            Name: "Invalid Tax Product",
            UnitCode: "PC",
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 999, // Non-existent
            CategoryId: null,
            BrandId: null,
            PrimaryBarcode: null,
            InitialStock: 0m,
            ReorderLevel: 0m,
            Notes: null);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.CreateProductAsync(request, "admin"));
        Assert.Equal("TAX_CATEGORY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateProduct_DuplicateBarcodeAcrossCatalog_ShouldThrowDomainException()
    {
        var request1 = new CreateProductRequest(
            Sku: "PROD-A",
            Name: "Product A",
            UnitCode: "PC",
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            PrimaryBarcode: "999988887777",
            InitialStock: 0m,
            ReorderLevel: 0m,
            Notes: null);

        await _service.CreateProductAsync(request1, "admin");

        var request2 = new CreateProductRequest(
            Sku: "PROD-B",
            Name: "Product B",
            UnitCode: "PC",
            CostPricePaise: 100,
            SellingPricePaise: 150,
            TaxCategoryId: 1,
            CategoryId: null,
            BrandId: null,
            PrimaryBarcode: "999988887777",
            InitialStock: 0m,
            ReorderLevel: 0m,
            Notes: null);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.CreateProductAsync(request2, "admin"));
        Assert.Equal("BARCODE_ALREADY_EXISTS", ex.ErrorCode);
    }

    [Fact]
    public async Task AddBarcode_ShouldPromoteNewPrimaryAndDemoteOld()
    {
        var product = new Product(
            Guid.NewGuid(),
            "BARCODE-MGMT",
            "Barcode Management",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(100),
            Money.FromMinorUnits(150),
            taxCategoryId: 1);
        product.AddBarcode(new Barcode("111111111111"), isPrimary: true);
        await _productRepo.AddAsync(product);

        var result = await _service.AddBarcodeAsync(product.Id, new AddBarcodeRequest("222222222222", IsPrimary: true), "admin");
        Assert.True(result.Success);

        var updated = await _productRepo.GetByIdAsync(product.Id);
        Assert.NotNull(updated);
        var b1 = updated.Barcodes.First(b => b.Barcode == "111111111111");
        var b2 = updated.Barcodes.First(b => b.Barcode == "222222222222");
        Assert.False(b1.IsPrimary);
        Assert.True(b2.IsPrimary);
    }
}