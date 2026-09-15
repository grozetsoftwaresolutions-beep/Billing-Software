using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RetailPOS.Contracts.Discovery;

namespace RetailPOS.Infrastructure.Security;

public static class ChallengeProtocolHelper
{
    public const string NistP256CurveOrderHex = "00FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551";
    public static readonly BigInteger P256CurveOrder = BigInteger.Parse(NistP256CurveOrderHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public static readonly BigInteger P256HalfCurveOrder = P256CurveOrder / 2;

    public static string BuildCanonicalPayload(
        string purpose,
        string nonce,
        DateTimeOffset clientTimestampUtc,
        string serverId,
        Guid storeId,
        string serverFingerprint,
        string publicKeySpkiSha256)
    {
        // Strictly 8 lines separated by \n (0x0A), UTF-8 without BOM
        var sb = new StringBuilder();
        sb.Append(ChallengeConstants.DomainSeparator).Append('\n');
        sb.Append(purpose).Append('\n');
        sb.Append(nonce.Trim().ToUpperInvariant()).Append('\n');
        sb.Append(clientTimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(serverId.Trim()).Append('\n');
        sb.Append(storeId.ToString("D").ToLowerInvariant()).Append('\n');
        sb.Append(serverFingerprint.Trim().ToUpperInvariant()).Append('\n');
        sb.Append(publicKeySpkiSha256.Trim().ToUpperInvariant());
        return sb.ToString();
    }

    public static byte[] SignCanonical(ECDsa privateKey, string canonicalPayload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        byte[] signatureBytes = privateKey.SignData(payloadBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        if (signatureBytes.Length != 64)
            throw new InvalidOperationException("Generated signature does not match IEEE P1363 64-byte length.");

        // Normalize S to canonical Low-S form (S <= curveOrder / 2):
        BigInteger s = new BigInteger(signatureBytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (s > P256HalfCurveOrder)
        {
            BigInteger sLow = P256CurveOrder - s;
            Span<byte> sTarget = signatureBytes.AsSpan(32, 32);
            sTarget.Clear();
            byte[] sBytes = sLow.ToByteArray(isUnsigned: true, isBigEndian: true);
            sBytes.CopyTo(sTarget.Slice(32 - sBytes.Length));
        }

        return signatureBytes;
    }

    public static bool VerifyCanonical(
        ECDsa publicKey,
        string canonicalPayload,
        byte[] signatureBytes,
        out string? errorCode,
        out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;

        if (signatureBytes.Length != 64)
        {
            errorCode = "SERVER_IDENTITY_SIGNATURE_INVALID_LENGTH";
            errorMessage = $"Signature length must be exactly 64 bytes. Actual: {signatureBytes.Length}.";
            return false;
        }

        BigInteger r = new BigInteger(signatureBytes.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
        BigInteger s = new BigInteger(signatureBytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);

        if (r <= BigInteger.Zero || r >= P256CurveOrder)
        {
            errorCode = "SERVER_IDENTITY_SIGNATURE_R_OUT_OF_RANGE";
            errorMessage = "Signature R value is outside the valid curve range [1, n-1].";
            return false;
        }

        if (s <= BigInteger.Zero || s > P256HalfCurveOrder)
        {
            errorCode = "SERVER_IDENTITY_SIGNATURE_NON_CANONICAL_HIGH_S";
            errorMessage = "Signature S value is non-canonical (High-S). Strict Low-S canonicality required.";
            return false;
        }

        byte[] payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        bool isValid = publicKey.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        if (!isValid)
        {
            errorCode = "SERVER_IDENTITY_SIGNATURE_INVALID";
            errorMessage = "ECDSA P-256 signature verification failed against public key.";
            return false;
        }

        return true;
    }

    public static byte[] MalleateToHighS(byte[] canonicalLowSSignature)
    {
        if (canonicalLowSSignature.Length != 64)
            throw new ArgumentException("Signature must be 64 bytes.", nameof(canonicalLowSSignature));

        byte[] result = new byte[64];
        Buffer.BlockCopy(canonicalLowSSignature, 0, result, 0, 32);

        BigInteger s = new BigInteger(canonicalLowSSignature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        BigInteger highS = P256CurveOrder - s;

        Span<byte> sTarget = result.AsSpan(32, 32);
        sTarget.Clear();
        byte[] highSBytes = highS.ToByteArray(isUnsigned: true, isBigEndian: true);
        highSBytes.CopyTo(sTarget.Slice(32 - highSBytes.Length));

        return result;
    }
}

public sealed class NonceReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cache = new();
    private DateTimeOffset _lastPurge = DateTimeOffset.UtcNow;
    private readonly object _purgeLock = new();

    public bool TryValidateAndRecordNonce(
        string nonce,
        DateTimeOffset clientTimestampUtc,
        out string? errorCode,
        out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length != 64)
        {
            errorCode = "INVALID_NONCE_FORMAT";
            errorMessage = "Nonce must be exactly 64 hexadecimal characters.";
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        // Challenge Validity Window = 60 Seconds
        var delta = (now - clientTimestampUtc).Duration();
        if (delta > TimeSpan.FromSeconds(ChallengeConstants.DefaultValiditySeconds))
        {
            errorCode = "STALE_CHALLENGE_TIMESTAMP";
            errorMessage = $"Challenge timestamp is outside the allowable 60-second validity window (Delta: {delta.TotalSeconds:F1}s).";
            return false;
        }

        // Replay-Cache Retention = 90 Seconds (Intentional Asymmetry)
        var expiresAt = now.AddSeconds(ChallengeConstants.DefaultRetentionSeconds);
        string normalizedNonce = nonce.Trim().ToUpperInvariant();

        if (!_cache.TryAdd(normalizedNonce, expiresAt))
        {
            errorCode = "REPLAYED_CHALLENGE_NONCE";
            errorMessage = "Challenge nonce has already been used and cannot be replayed.";
            return false;
        }

        // Sweep expired items periodically
        if (now - _lastPurge > TimeSpan.FromSeconds(30))
        {
            lock (_purgeLock)
            {
                if (now - _lastPurge > TimeSpan.FromSeconds(30))
                {
                    _lastPurge = now;
                    foreach (var kvp in _cache)
                    {
                        if (kvp.Value < now)
                        {
                            _cache.TryRemove(kvp.Key, out _);
                        }
                    }
                }
            }
        }

        return true;
    }
}
