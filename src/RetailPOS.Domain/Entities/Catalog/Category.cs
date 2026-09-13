using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Catalog;

public sealed class Category : BaseEntity<int>
{
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }

    public Category(int id, string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Category name cannot be empty.", "INVALID_CATEGORY_NAME");

        Id = id;
        Name = name.Trim();
        Description = description?.Trim();
        IsActive = true;
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}
