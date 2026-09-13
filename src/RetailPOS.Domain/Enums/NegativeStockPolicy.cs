namespace RetailPOS.Domain.Enums;

/// <summary>
/// Store configuration policy for whether sales can cause negative stock balances.
/// </summary>
public enum NegativeStockPolicy
{
    Disallowed = 1,
    Allowed = 2
}
