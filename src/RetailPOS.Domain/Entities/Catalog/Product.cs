using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Catalog;

public sealed class Product : BaseEntity<Guid>
{
    public string SKU { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public int? CategoryId { get; private set; }
    public int? BrandId { get; private set; }
    public UnitOfMeasure Unit { get; private set; } = null!;
    public Money CostPrice { get; private set; }
    public Money SellingPrice { get; private set; }
    public int TaxCategoryId { get; private set; }
    public bool IsActive { get; private set; }
    public string? Notes { get; private set; }

    private readonly List<ProductBarcode> _barcodes = [];
    public IReadOnlyList<ProductBarcode> Barcodes => _barcodes.AsReadOnly();

    // Required by EF Core
    private Product() { }

    public Product(
        Guid id,
        string sku,
        string name,
        UnitOfMeasure unit,
        Money costPrice,
        Money sellingPrice,
        int taxCategoryId,
        int? categoryId = null,
        int? brandId = null,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new DomainException("Product SKU cannot be empty.", "INVALID_SKU");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Product name cannot be empty.", "INVALID_PRODUCT_NAME");

        Id = id;
        SKU = sku.Trim().ToUpperInvariant();
        Name = name.Trim();
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        CostPrice = costPrice;
        SellingPrice = sellingPrice;
        TaxCategoryId = taxCategoryId;
        CategoryId = categoryId;
        BrandId = brandId;
        Notes = notes?.Trim();
        IsActive = true;
    }

    public void AddBarcode(Barcode barcode, bool isPrimary = false)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        var pb = new ProductBarcode(0, Id, barcode, isPrimary);
        _barcodes.Add(pb);
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdatePrices(Money newCostPrice, Money newSellingPrice)
    {
        if (newSellingPrice.IsNegative)
            throw new DomainException("Selling price cannot be negative.", "INVALID_SELLING_PRICE");

        CostPrice = newCostPrice;
        SellingPrice = newSellingPrice;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
