using System.Text.RegularExpressions;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Catalog;

public sealed partial class Product : BaseEntity<Guid>
{
    [GeneratedRegex(@"^[A-Z0-9_-]+$")]
    private static partial Regex SkuRegex();

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
        ValidateSku(sku);
        ValidateName(name);
        ValidatePrices(costPrice, sellingPrice);
        ValidateNotes(notes);

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

    public void UpdateDetails(
        string name,
        UnitOfMeasure unit,
        int taxCategoryId,
        int? categoryId,
        int? brandId,
        string? notes)
    {
        ValidateName(name);
        ValidateNotes(notes);

        Name = name.Trim();
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        TaxCategoryId = taxCategoryId;
        CategoryId = categoryId;
        BrandId = brandId;
        Notes = notes?.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdatePrices(Money newCostPrice, Money newSellingPrice)
    {
        ValidatePrices(newCostPrice, newSellingPrice);

        CostPrice = newCostPrice;
        SellingPrice = newSellingPrice;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public ProductBarcode AddBarcode(Barcode barcode, bool isPrimary = false)
    {
        ArgumentNullException.ThrowIfNull(barcode);

        if (_barcodes.Any(b => b.Barcode == barcode.Value))
            throw new DomainException("Barcode already registered on this product.", "DUPLICATE_BARCODE");

        // Enforce single primary barcode invariant: atomically demote existing primary
        if (isPrimary)
        {
            foreach (var existing in _barcodes.Where(b => b.IsPrimary))
            {
                existing.DemotePrimary();
            }
        }

        var pb = new ProductBarcode(0, Id, barcode, isPrimary);
        _barcodes.Add(pb);
        UpdatedAtUtc = DateTime.UtcNow;
        return pb;
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

    private static void ValidateSku(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new DomainException("Product SKU cannot be empty.", "INVALID_SKU");

        var trimmed = sku.Trim();
        if (trimmed.Length > 50 || !SkuRegex().IsMatch(trimmed))
            throw new DomainException("Product SKU must be uppercase alphanumeric, hyphen, or underscore and at most 50 characters.", "INVALID_SKU");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Product name cannot be empty.", "INVALID_PRODUCT_NAME");

        if (name.Trim().Length > 200)
            throw new DomainException("Product name cannot exceed 200 characters.", "INVALID_PRODUCT_NAME");
    }

    private static void ValidatePrices(Money costPrice, Money sellingPrice)
    {
        if (costPrice.IsNegative)
            throw new DomainException("Cost price cannot be negative.", "INVALID_COST_PRICE");

        if (sellingPrice.IsNegative)
            throw new DomainException("Selling price cannot be negative.", "INVALID_SELLING_PRICE");
    }

    private static void ValidateNotes(string? notes)
    {
        if (notes != null && notes.Trim().Length > 500)
            throw new DomainException("Notes cannot exceed 500 characters.", "NOTES_TOO_LONG");
    }
}