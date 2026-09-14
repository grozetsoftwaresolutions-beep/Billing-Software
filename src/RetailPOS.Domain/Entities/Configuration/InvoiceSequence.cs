using System.Globalization;
using RetailPOS.Domain.Common;

namespace RetailPOS.Domain.Entities.Configuration;

public sealed class InvoiceSequence : BaseEntity<string>
{
    public string SequenceName { get; private set; } = null!;
    public string Prefix { get; private set; } = null!;
    public string PeriodDate { get; private set; } = null!;
    public long CurrentValue { get; private set; }

    private InvoiceSequence() { }

    public InvoiceSequence(string sequenceName, string prefix = "INV", long initialValue = 0)
    {
        if (string.IsNullOrWhiteSpace(sequenceName))
            throw new DomainException("Sequence name cannot be empty.", "INVALID_SEQUENCE_NAME");

        SequenceName = sequenceName.Trim().ToUpperInvariant();
        Prefix = (prefix ?? "INV").Trim().ToUpperInvariant();
        PeriodDate = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Id = $"{SequenceName}-{PeriodDate}";
        CurrentValue = initialValue;
    }

    public InvoiceSequence(string sequenceName, string prefix, string periodDate, long initialValue = 0)
    {
        if (string.IsNullOrWhiteSpace(sequenceName))
            throw new DomainException("Sequence name cannot be empty.", "INVALID_SEQUENCE_NAME");

        if (string.IsNullOrWhiteSpace(periodDate))
            throw new DomainException("Period date cannot be empty.", "INVALID_PERIOD_DATE");

        SequenceName = sequenceName.Trim().ToUpperInvariant();
        Prefix = (prefix ?? "INV").Trim().ToUpperInvariant();
        PeriodDate = periodDate.Trim();
        Id = $"{SequenceName}-{PeriodDate}";
        CurrentValue = initialValue;
    }

    public string NextNumber()
    {
        CurrentValue++;
        UpdatedAtUtc = DateTime.UtcNow;
        return string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2:D6}", Prefix, PeriodDate, CurrentValue);
    }

    public string NextInvoiceNumber() => NextNumber();
}
