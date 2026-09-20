namespace RetailPOS.Contracts.Discovery;

public static class ChallengeConstants
{
    public const string DomainSeparator = "GrozetPOS-ServerIdentity-Challenge-v1";
    public const int DefaultValiditySeconds = 60;
    public const int DefaultRetentionSeconds = 90;
}

public static class ChallengePurposes
{
    public const string ServerIdentityAuthentication = "ServerIdentityAuthentication";
    public const string ServerIdentityRePinning = "ServerIdentityRePinning";
}

public sealed record ServerDiscoveryResponseDto(
    string ServerId,
    Guid StoreId,
    string StoreName,
    string ServerFingerprint,
    string PublicKeySpki,
    string HttpBaseUrl,
    string? HttpsBaseUrl,
    string ServerVersion,
    DateTimeOffset ServerTimeUtc,
    int ProtocolVersion = 1,
    int MinSupportedClientProtocolVersion = 1);

public sealed record ServerChallengeRequestDto(
    string Nonce,
    string Purpose,
    DateTimeOffset ClientTimestampUtc);

public sealed record ServerChallengeResponseDto(
    string Nonce,
    string Purpose,
    string ServerId,
    Guid StoreId,
    string ServerFingerprint,
    string PublicKeySpkiBase64,
    string SignatureBase64,
    DateTimeOffset ServerTimestampUtc);

public sealed record NetworkDiagnosticsDto(
    string MachineName,
    IReadOnlyList<NetworkInterfaceInfoDto> Interfaces,
    int DiscoveryPort,
    int HttpPort,
    int? HttpsPort);

public sealed record NetworkInterfaceInfoDto(
    string Name,
    string Description,
    string IpAddress,
    string SubnetMask,
    bool IsLoopback,
    bool SupportsMulticast);
