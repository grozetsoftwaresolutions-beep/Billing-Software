using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Taxes;

/// <summary>
/// Sub-component of a tax rate (e.g. CGST 2.5%, SGST 2.5% forming a 5% GST rate).
/// Generic across global retail tax structures.
/// </summary>
public sealed record TaxComponent
{
    public string Name { get; }
    public decimal RatePercentage { get; }

    public TaxComponent(string name, decimal ratePercentage)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Tax component name cannot be empty.", "INVALID_TAX_COMPONENT");
        if (ratePercentage < 0m || ratePercentage > 100m)
            throw new DomainException($"Tax rate percentage must be between 0 and 100 (got {ratePercentage}).", "INVALID_TAX_RATE");

        Name = name.Trim();
        RatePercentage = ratePercentage;
    }
}
