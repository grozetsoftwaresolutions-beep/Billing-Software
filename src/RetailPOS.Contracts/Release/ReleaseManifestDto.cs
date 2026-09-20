using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RetailPOS.Contracts.Release;

/// <summary>
/// Strongly typed contract representing the release manifest for GROZET RetailPOS.
/// Authenticated via companion detached CMS/PKCS#7 signature (release_manifest.json.p7s).
/// </summary>
public sealed class ReleaseManifestDto
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://grozet.com/schemas/release-manifest-v1.json";

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = "Grozet Offline Retail POS & Billing";

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "1.0.0";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 1;

    [JsonPropertyName("databaseSchemaVersion")]
    public int DatabaseSchemaVersion { get; set; } = 15;

    [JsonPropertyName("gitReleaseTag")]
    public string GitReleaseTag { get; set; } = string.Empty;

    [JsonPropertyName("gitCommitHash")]
    public string GitCommitHash { get; set; } = string.Empty;

    [JsonPropertyName("buildConfiguration")]
    public string BuildConfiguration { get; set; } = "Release";

    [JsonPropertyName("buildTimestampUtc")]
    public DateTime BuildTimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("signingMetadata")]
    public ReleaseSigningMetadataDto SigningMetadata { get; set; } = new();

    [JsonPropertyName("artifacts")]
    public List<ReleaseArtifactEntryDto> Artifacts { get; set; } = new();
}

/// <summary>
/// Signing and timestamp provenance metadata for the release package.
/// </summary>
public sealed class ReleaseSigningMetadataDto
{
    [JsonPropertyName("isSigned")]
    public bool IsSigned { get; set; }

    [JsonPropertyName("signerSubject")]
    public string SignerSubject { get; set; } = string.Empty;

    [JsonPropertyName("signerThumbprint")]
    public string SignerThumbprint { get; set; } = string.Empty;

    [JsonPropertyName("timestampAuthority")]
    public string TimestampAuthority { get; set; } = string.Empty;

    [JsonPropertyName("signatureFormat")]
    public string SignatureFormat { get; set; } = "CMS_PKCS7_Detached";

    [JsonPropertyName("signatureFileName")]
    public string SignatureFileName { get; set; } = "release_manifest.json.p7s";
}

/// <summary>
/// Metadata, classification, and SHA-256 integrity hash for an artifact in the release package.
/// </summary>
public sealed class ReleaseArtifactEntryDto
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("peClassification")]
    public string PeClassification { get; set; } = string.Empty;

    [JsonPropertyName("authenticodeStatus")]
    public string AuthenticodeStatus { get; set; } = string.Empty;

    [JsonPropertyName("isTimestamped")]
    public bool IsTimestamped { get; set; }
}
