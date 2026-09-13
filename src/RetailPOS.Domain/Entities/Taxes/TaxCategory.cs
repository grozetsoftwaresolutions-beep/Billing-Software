using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Taxes;

public sealed class TaxCategory : BaseEntity<int>
{
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public bool IsExempt { get; private set; }
    public bool IsActive { get; private set; }

    private readonly List<TaxComponent> _components = [];
    public IReadOnlyList<TaxComponent> Components => _components.AsReadOnly();

    public decimal TotalRatePercentage => IsExempt ? 0m : _components.Sum(c => c.RatePercentage);

    private TaxCategory() { }

    public TaxCategory(int id, string code, string name, bool isExempt = false, IEnumerable<TaxComponent>? components = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("Tax category code cannot be empty.", "INVALID_TAX_CATEGORY_CODE");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Tax category name cannot be empty.", "INVALID_TAX_CATEGORY_NAME");

        Id = id;
        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        IsExempt = isExempt;
        IsActive = true;

        if (components != null)
        {
            _components.AddRange(components);
        }
    }

    public void AddComponent(TaxComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        _components.Add(component);
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public static readonly TaxCategory ZeroRated = new(1, "GST_0", "GST 0%", false, [new TaxComponent("Zero Tax", 0m)]);
    public static readonly TaxCategory Exempt = new(2, "EXEMPT", "Tax Exempt", true);
}
