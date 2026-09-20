namespace RetailPOS.Contracts.Common;

/// <summary>
/// Authoritative protocol and schema version constants.
/// Enforces separation between user-visible Product/Application Version,
/// multi-counter Client/Server Protocol Version, and authoritative Database Schema Version.
/// </summary>
public static class ProtocolVersionConstants
{
    /// <summary>
    /// Mandatory HTTP header transmitting the client terminal's protocol version integer.
    /// </summary>
    public const string ClientProtocolVersionHeader = "X-Client-Protocol-Version";

    /// <summary>
    /// Optional/diagnostic HTTP header transmitting the client terminal's semver string.
    /// </summary>
    public const string ClientVersionHeader = "X-Client-Version";

    /// <summary>
    /// Current server protocol version integer.
    /// </summary>
    public const int CurrentProtocolVersion = 1;

    /// <summary>
    /// Minimum client protocol version accepted by this server build.
    /// </summary>
    public const int MinSupportedClientProtocolVersion = 1;

    /// <summary>
    /// Baseline schema version established through Sub-Phase 16.5.
    /// </summary>
    public const int BaselineSchemaVersion = 14;

    /// <summary>
    /// Target authoritative database schema version for Sub-Phase 16.6.
    /// </summary>
    public const int TargetSchemaVersion = 15;
}
