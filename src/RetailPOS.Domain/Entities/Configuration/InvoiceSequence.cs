using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Configuration;

public sealed class InvoiceSequence : BaseEntity<string>
{
    public string SequenceName { get; private set; } = null!;
    public string Prefix { get; private set; } = null!;
    public long CurrentValue { get; private set; }

    private InvoiceSequence() { }

    public InvoiceSequence(string sequenceName, string prefix = "INV", long initialValue = 0)
    {
        if (string.IsNullOrWhiteSpace(sequenceName))
            throw new DomainException("Sequence name cannot be empty.", "INVALID_SEQUENCE_NAME");

        Id = sequenceName.Trim().ToUpperInvariant();
        SequenceName = Id;
        Prefix = prefix.Trim().ToUpperInvariant();
        CurrentValue = initialValue;
    }

    public string NextInvoiceNumber()
    {
        CurrentValue++;
        UpdatedAtUtc = DateTime.UtcNow;
        return $"{Prefix}-{DateTime.UtcNow:yyyyMMdd}-{CurrentValue:D6}";
    }
}
