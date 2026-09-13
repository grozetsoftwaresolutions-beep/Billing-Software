using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Catalog;

public sealed class Brand : BaseEntity<int>
{
    public string Name { get; private set; }
    public bool IsActive { get; private set; }

    public Brand(int id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Brand name cannot be empty.", "INVALID_BRAND_NAME");

        Id = id;
        Name = name.Trim();
        IsActive = true;
    }

    public void Deactivate() => IsActive = false;
}
