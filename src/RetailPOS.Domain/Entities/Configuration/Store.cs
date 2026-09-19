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

    public Guid StoreGuid { get; private set; } = Guid.Empty;
    public string? MasterKeyValidatorHash { get; private set; }
    public string? WrappedStoreSigningKey { get; private set; }
    public string? PendingRotationValidatorHash { get; private set; }
    public string? PendingWrappedStoreSigningKey { get; private set; }
    public string? RotationState { get; private set; }
    public string? RotationTokenHash { get; private set; }
    public DateTime? RotationTokenExpiresAtUtc { get; private set; }
    public string? RotationInitiatedByUserId { get; private set; }

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

    public void InitializeSecurityRoot(Guid storeGuid, string masterKeyValidatorHash, string wrappedStoreSigningKey)
    {
        if (storeGuid == Guid.Empty)
            throw new DomainException("Store GUID cannot be empty.", "INVALID_STORE_GUID");
        if (string.IsNullOrWhiteSpace(masterKeyValidatorHash) || masterKeyValidatorHash.Length != 64)
            throw new DomainException("Master key validator hash must be 64 hexadecimal characters.", "INVALID_VALIDATOR_HASH");
        if (string.IsNullOrWhiteSpace(wrappedStoreSigningKey) || wrappedStoreSigningKey.Length != 120)
            throw new DomainException("Wrapped store signing key must be 120 hexadecimal characters (60 bytes).", "INVALID_WRAPPED_KEY");

        StoreGuid = storeGuid;
        MasterKeyValidatorHash = masterKeyValidatorHash.ToUpperInvariant();
        WrappedStoreSigningKey = wrappedStoreSigningKey.ToUpperInvariant();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void StageRotation(
        string pendingValidatorHash,
        string pendingWrappedKey,
        string tokenHash,
        DateTime tokenExpiresAtUtc,
        string initiatedByUserId)
    {
        if (RotationState != null)
            throw new DomainException("A recovery key rotation is already pending or in progress.", "ROTATION_ALREADY_IN_PROGRESS");
        if (string.IsNullOrWhiteSpace(pendingValidatorHash) || pendingValidatorHash.Length != 64)
            throw new DomainException("Pending validator hash must be 64 hexadecimal characters.", "INVALID_PENDING_HASH");
        if (string.IsNullOrWhiteSpace(pendingWrappedKey) || pendingWrappedKey.Length != 120)
            throw new DomainException("Pending wrapped key must be 120 hexadecimal characters.", "INVALID_PENDING_WRAPPED_KEY");
        if (string.IsNullOrWhiteSpace(tokenHash) || tokenHash.Length != 64)
            throw new DomainException("Token hash must be 64 hexadecimal characters.", "INVALID_TOKEN_HASH");
        if (string.IsNullOrWhiteSpace(initiatedByUserId))
            throw new DomainException("Initiating user ID cannot be empty.", "INVALID_USER_ID");

        PendingRotationValidatorHash = pendingValidatorHash.ToUpperInvariant();
        PendingWrappedStoreSigningKey = pendingWrappedKey.ToUpperInvariant();
        RotationTokenHash = tokenHash.ToLowerInvariant();
        RotationTokenExpiresAtUtc = tokenExpiresAtUtc;
        RotationInitiatedByUserId = initiatedByUserId;
        RotationState = "ROTATION_STAGED";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RequestRotationCommit()
    {
        if (RotationState != "ROTATION_STAGED")
            throw new DomainException("No staged recovery key rotation exists to commit.", "NO_STAGED_ROTATION");

        RotationState = "ROTATION_COMMIT_REQUESTED";
        RotationTokenHash = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void FinalizeRotation()
    {
        if (RotationState != "ROTATION_COMMIT_REQUESTED" && RotationState != "ROTATION_STAGED")
            throw new DomainException("Rotation commit was not requested.", "ROTATION_NOT_REQUESTED");
        if (string.IsNullOrWhiteSpace(PendingRotationValidatorHash) || string.IsNullOrWhiteSpace(PendingWrappedStoreSigningKey))
            throw new DomainException("Pending rotation data is missing.", "MISSING_PENDING_DATA");

        MasterKeyValidatorHash = PendingRotationValidatorHash;
        WrappedStoreSigningKey = PendingWrappedStoreSigningKey;
        PendingRotationValidatorHash = null;
        PendingWrappedStoreSigningKey = null;
        RotationState = null;
        RotationTokenHash = null;
        RotationTokenExpiresAtUtc = null;
        RotationInitiatedByUserId = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void CancelRotation()
    {
        if (RotationState == null && PendingRotationValidatorHash == null)
            return;

        PendingRotationValidatorHash = null;
        PendingWrappedStoreSigningKey = null;
        RotationState = null;
        RotationTokenHash = null;
        RotationTokenExpiresAtUtc = null;
        RotationInitiatedByUserId = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void ReKeyRestoredDatabase(string newValidatorHash, string newWrappedKey)
    {
        if (string.IsNullOrWhiteSpace(newValidatorHash) || newValidatorHash.Length != 64)
            throw new DomainException("Validator hash must be 64 hexadecimal characters.", "INVALID_VALIDATOR_HASH");
        if (string.IsNullOrWhiteSpace(newWrappedKey) || newWrappedKey.Length != 120)
            throw new DomainException("Wrapped key must be 120 hexadecimal characters.", "INVALID_WRAPPED_KEY");

        MasterKeyValidatorHash = newValidatorHash.ToUpperInvariant();
        WrappedStoreSigningKey = newWrappedKey.ToUpperInvariant();
        PendingRotationValidatorHash = null;
        PendingWrappedStoreSigningKey = null;
        RotationState = null;
        RotationTokenHash = null;
        RotationTokenExpiresAtUtc = null;
        RotationInitiatedByUserId = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

