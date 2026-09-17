using System;
using System.Security.Cryptography;
using System.Text;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;

namespace RetailPOS.Application.Security;

public sealed class F2RecoveryService : IF2RecoveryService
{
    public (F2RecoveryArtifactDto Artifact, string ConfirmationCode) GenerateRecoveryArtifact(
        Guid storeId,
        Guid terminalId,
        Guid rotationOperationId,
        string candidateF2Fingerprint,
        string nonce,
        DateTime validUntilUtc,
        byte[] tmk,
        ECDsa rootPrivateKey)
    {
        if (storeId == Guid.Empty) throw new DomainException("StoreId cannot be empty.", "INVALID_STORE_ID");
        if (terminalId == Guid.Empty) throw new DomainException("TerminalId cannot be empty.", "INVALID_TERMINAL_ID");
        if (rotationOperationId == Guid.Empty) throw new DomainException("RotationOperationId cannot be empty.", "INVALID_ROTATION_ID");
        if (string.IsNullOrWhiteSpace(candidateF2Fingerprint)) throw new DomainException("Candidate F2 fingerprint cannot be empty.", "INVALID_FINGERPRINT");
        if (string.IsNullOrWhiteSpace(nonce)) throw new DomainException("Nonce cannot be empty.", "INVALID_NONCE");
        if (tmk == null || tmk.Length != 32) throw new DomainException("TMK must be exactly 32 bytes.", "INVALID_TMK");
        if (rootPrivateKey == null) throw new DomainException("Root private key cannot be null.", "INVALID_KEY");

        string canonicalPayload = FormatCanonicalPayload(storeId, terminalId, rotationOperationId, candidateF2Fingerprint, nonce, validUntilUtc);
        byte[] canonicalBytes = Encoding.UTF8.GetBytes(canonicalPayload);

        // ECDSA P-256 with IEEE P1363 fixed-field concatenation format = exactly 64 bytes
        byte[] signature = rootPrivateKey.SignData(canonicalBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (signature.Length != 64)
            throw new CryptographicException($"Generated signature length {signature.Length} is not 64 bytes IEEE P1363.");

        // Confirmation code derived via HMAC-SHA256(canonicalBytes, TMK)[0..4] = 40 bits = 8 Crockford Base32 characters
        byte[] hmac = HMACSHA256.HashData(tmk, canonicalBytes);
        byte[] codeBytes = new byte[5];
        Array.Copy(hmac, 0, codeBytes, 0, 5);
        string rawCode = CrockfordBase32.Encode(codeBytes);
        string confirmationCode = CrockfordBase32.FormatGroups(rawCode, 4);

        var artifact = new F2RecoveryArtifactDto
        {
            StoreId = storeId,
            TerminalId = terminalId,
            RotationOperationId = rotationOperationId,
            CandidateF2Fingerprint = candidateF2Fingerprint,
            Nonce = nonce,
            ValidUntilUtc = validUntilUtc,
            SignatureBase64 = Convert.ToBase64String(signature)
        };

        return (artifact, confirmationCode);
    }

    public string VerifyAndApplyRecoveryArtifact(
        F2RecoveryArtifactDto artifact,
        string confirmationCode,
        Guid localStoreId,
        Guid localTerminalId,
        byte[] localTmk,
        byte[] pinnedPublicKeySpki)
    {
        if (artifact == null) throw new DomainException("Artifact cannot be null.", "INVALID_ARTIFACT");
        if (localStoreId != artifact.StoreId)
            throw new DomainException("StoreId mismatch in recovery artifact.", "ERROR_STORE_MISMATCH");
        if (localTerminalId != artifact.TerminalId)
            throw new DomainException("TerminalId mismatch in recovery artifact.", "ERROR_TERMINAL_MISMATCH");
        if (DateTime.UtcNow > artifact.ValidUntilUtc)
            throw new DomainException("Recovery artifact expired.", "ERROR_RECOVERY_EXPIRED");

        if (string.IsNullOrWhiteSpace(artifact.SignatureBase64))
            throw new DomainException("Signature is missing.", "ERROR_INVALID_SIGNATURE");

        byte[] sigBytes;
        try
        {
            sigBytes = Convert.FromBase64String(artifact.SignatureBase64);
        }
        catch (FormatException)
        {
            throw new DomainException("Signature is not valid Base64.", "ERROR_INVALID_SIGNATURE");
        }

        // Strict IEEE P1363 fixed-field concatenation check: exactly 64 bytes (rejects DER and truncated signatures)
        if (sigBytes.Length != 64)
            throw new DomainException($"Signature must be exactly 64 bytes IEEE P1363 (got {sigBytes.Length}). DER and truncated formats are rejected.", "ERROR_INVALID_SIGNATURE_FORMAT");

        string canonicalPayload = FormatCanonicalPayload(
            artifact.StoreId,
            artifact.TerminalId,
            artifact.RotationOperationId,
            artifact.CandidateF2Fingerprint,
            artifact.Nonce,
            artifact.ValidUntilUtc);
        byte[] canonicalBytes = Encoding.UTF8.GetBytes(canonicalPayload);

        // 1. Verify ECDSA signature against pinned PublicKeySpki
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(pinnedPublicKeySpki, out _);
            bool sigValid = ecdsa.VerifyData(canonicalBytes, sigBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!sigValid)
                throw new DomainException("ECDSA root signature verification failed.", "ERROR_INVALID_SIGNATURE");
        }

        // 2. Verify ConfirmationCode via HMAC-SHA256 using Counter's local TMK
        byte[] hmac = HMACSHA256.HashData(localTmk, canonicalBytes);
        byte[] codeBytes = new byte[5];
        Array.Copy(hmac, 0, codeBytes, 0, 5);
        string expectedRawCode = CrockfordBase32.Encode(codeBytes);
        string expectedNormCode = CrockfordBase32.Normalize(expectedRawCode);
        string submittedNormCode = CrockfordBase32.Normalize(confirmationCode);

        if (!string.Equals(submittedNormCode, expectedNormCode, StringComparison.Ordinal))
            throw new DomainException("Owner confirmation code mismatch.", "ERROR_CONFIRMATION_CODE_MISMATCH");

        // Verification succeeded! Return promoted F2 fingerprint
        return artifact.CandidateF2Fingerprint;
    }

    private static string FormatCanonicalPayload(
        Guid storeId,
        Guid terminalId,
        Guid rotationOperationId,
        string candidateF2Fingerprint,
        string nonce,
        DateTime validUntilUtc)
    {
        return $"RETAILPOS-ROTATION-v5.2.9.6\n{storeId}\n{terminalId}\n{rotationOperationId}\n{candidateF2Fingerprint}\n{nonce}\n{validUntilUtc.Ticks}";
    }
}
