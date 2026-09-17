using System;
using System.Security.Cryptography;
using RetailPOS.Contracts.Terminals;

namespace RetailPOS.Application.Security;

public interface IF2RecoveryService
{
    (F2RecoveryArtifactDto Artifact, string ConfirmationCode) GenerateRecoveryArtifact(
        Guid storeId,
        Guid terminalId,
        Guid rotationOperationId,
        string candidateF2Fingerprint,
        string nonce,
        DateTime validUntilUtc,
        byte[] tmk,
        ECDsa rootPrivateKey);

    string VerifyAndApplyRecoveryArtifact(
        F2RecoveryArtifactDto artifact,
        string confirmationCode,
        Guid localStoreId,
        Guid localTerminalId,
        byte[] localTmk,
        byte[] pinnedPublicKeySpki);
}
