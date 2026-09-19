using System;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Terminals;

namespace RetailPOS.Application.Security.Srp6a;

public interface ISrp6aPairingService
{
    Task<(Guid PairingOperationId, string HumanPairingCode, byte[] Salt)> CreatePairingSessionAsync(
        Guid storeId,
        Guid terminalId,
        CancellationToken ct = default);

    Task<byte[]> InitiatePairingAsync(
        Guid pairingOperationId,
        byte[] clientEphemeralA,
        CancellationToken ct = default);

    Task<(EncryptedTmkDto EncryptedTmk, string ServerEvidenceM2, string PublicKeySpki)> ConfirmPairingAsync(
        Guid pairingOperationId,
        byte[] clientEvidenceM1,
        string sasConfirmationCode,
        string candidateF1,
        byte[] retryAuthToken,
        byte[] tmkBytes,
        string activeServerFingerprint,
        string publicKeySpkiBase64,
        CancellationToken ct = default);

    Task<(EncryptedTmkDto EncryptedTmk, string ServerEvidenceM2, string PublicKeySpki)> RetryPairingAsync(
        Guid pairingOperationId,
        Guid storeId,
        Guid terminalId,
        string candidateF1,
        byte[] retryAuthToken,
        string activeServerFingerprint,
        string publicKeySpkiBase64,
        CancellationToken ct = default);

    Task FinalizePairingAsync(
        Guid pairingOperationId,
        byte[] finalizeProof,
        byte[]? kPake = null,
        CancellationToken ct = default);
}
