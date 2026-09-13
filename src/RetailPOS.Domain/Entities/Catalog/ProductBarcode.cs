using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Catalog;

public sealed class ProductBarcode : BaseEntity<int>
{
    public Guid ProductId { get; private set; }
    public Barcode Barcode { get; private set; } = null!;
    public bool IsPrimary { get; private set; }
    public bool IsActive { get; private set; }

    private ProductBarcode() { }

    public ProductBarcode(int id, Guid productId, Barcode barcode, bool isPrimary = false)
    {
        Id = id;
        ProductId = productId;
        Barcode = barcode ?? throw new ArgumentNullException(nameof(barcode));
        IsPrimary = isPrimary;
        IsActive = true;
    }

    public void PromotePrimary()
    {
        IsPrimary = true;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void DemotePrimary()
    {
        IsPrimary = false;
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
