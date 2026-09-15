namespace RetailPOS.Domain.ValueObjects;

public sealed record ServerIdentityInfo
{
    public string ServerId { get; init; } = string.Empty;
    public Guid StoreId { get; init; }
    public string ServerFingerprint { get; init; } = string.Empty;
    public string PublicKeySpki { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }

    public string HumanVerifiableFingerprint => FormatHumanFingerprint(ServerFingerprint);

    public static string FormatHumanFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length < 16)
            return fingerprint ?? string.Empty;

        string clean = fingerprint.Replace("-", "").Trim().ToUpperInvariant();
        if (clean.Length < 16)
            return clean;

        return $"{clean[0..4]}-{clean[4..8]}-{clean[8..12]}-{clean[12..16]}";
    }
}
