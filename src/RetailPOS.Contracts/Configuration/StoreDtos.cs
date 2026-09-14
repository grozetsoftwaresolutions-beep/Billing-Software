using RetailPOS.Domain.Enums;

namespace RetailPOS.Contracts.Configuration;

public sealed record StoreSettingsDto(
    int Id,
    string StoreName,
    string? Address,
    string? Phone,
    string? TaxRegistrationNumber,
    string? ReceiptFooterMessage,
    PricingModel DefaultPricingModel,
    NegativeStockPolicy NegativeStockPolicy,
    bool CashRoundingEnabled,
    string TimeZoneId);

public sealed record UpdateStoreSettingsRequest(
    string? StoreName,
    string? Address,
    string? Phone,
    string? TaxRegistrationNumber,
    string? ReceiptFooterMessage,
    bool? CashRoundingEnabled,
    string? TimeZoneId);
