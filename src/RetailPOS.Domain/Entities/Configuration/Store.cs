using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Configuration;

public sealed class Store : BaseEntity<int>
{
    public string StoreName { get; private set; } = null!;
    public string? Address { get; private set; }
    public string? Phone { get; private set; }
    public string? TaxRegistrationNumber { get; private set; }
    public string? ReceiptFooterMessage { get; private set; }
    public PricingModel DefaultPricingModel { get; private set; }
    public NegativeStockPolicy NegativeStockPolicy { get; private set; }
    public Currency Currency { get; private set; } = null!;
    public bool CashRoundingEnabled { get; private set; } = true;
    public string TimeZoneId { get; private set; } = "India Standard Time";

    private Store() { }

    public Store(
        int id,
        string storeName,
        string? address = null,
        string? phone = null,
        string? taxRegistrationNumber = null,
        string? receiptFooterMessage = null,
        PricingModel defaultPricingModel = PricingModel.TaxInclusive,
        NegativeStockPolicy negativeStockPolicy = NegativeStockPolicy.Disallowed,
        Currency? currency = null,
        bool cashRoundingEnabled = true,
        string? timeZoneId = null)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            throw new DomainException("Store name cannot be empty.", "INVALID_STORE_NAME");

        Id = id;
        StoreName = storeName.Trim();
        Address = address?.Trim();
        Phone = phone?.Trim();
        TaxRegistrationNumber = taxRegistrationNumber?.Trim();
        ReceiptFooterMessage = receiptFooterMessage?.Trim();
        DefaultPricingModel = defaultPricingModel;
        NegativeStockPolicy = negativeStockPolicy;
        Currency = currency ?? Currency.INR;
        CashRoundingEnabled = cashRoundingEnabled;

        var tz = string.IsNullOrWhiteSpace(timeZoneId) ? "India Standard Time" : timeZoneId.Trim();
        ValidateWindowsTimeZone(tz);
        TimeZoneId = tz;
    }

    public void UpdateStoreName(string newStoreName)
    {
        if (string.IsNullOrWhiteSpace(newStoreName))
            throw new DomainException("Store name cannot be empty.", "INVALID_STORE_NAME");

        StoreName = newStoreName.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateDetails(string? address, string? phone, string? taxRegistrationNumber, string? receiptFooterMessage)
    {
        Address = address?.Trim();
        Phone = phone?.Trim();
        TaxRegistrationNumber = taxRegistrationNumber?.Trim();
        ReceiptFooterMessage = receiptFooterMessage?.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateBillingSettings(bool cashRoundingEnabled, string? timeZoneId = null)
    {
        CashRoundingEnabled = cashRoundingEnabled;
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            var tz = timeZoneId.Trim();
            ValidateWindowsTimeZone(tz);
            TimeZoneId = tz;
        }
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public static void ValidateWindowsTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new DomainException("Time zone ID cannot be empty.", "INVALID_TIMEZONE_ID");

        var trimmed = timeZoneId.Trim();
        if (trimmed.Contains('/'))
            throw new DomainException($"Invalid Windows Time Zone ID '{trimmed}'. IANA time zone identifiers are not allowed; use Windows Time Zone ID such as 'India Standard Time'.", "INVALID_TIMEZONE_ID");

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new DomainException($"Invalid Windows Time Zone ID '{trimmed}'. Must be a recognized Windows time zone ID.", "INVALID_TIMEZONE_ID");
        }
    }
}
