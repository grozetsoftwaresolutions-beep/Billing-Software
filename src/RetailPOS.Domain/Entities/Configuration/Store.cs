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
        Currency? currency = null)
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
    }

    public void UpdateStoreName(string newStoreName)
    {
        if (string.IsNullOrWhiteSpace(newStoreName))
            throw new DomainException("Store name cannot be empty.", "INVALID_STORE_NAME");

        StoreName = newStoreName.Trim();
    }
}
