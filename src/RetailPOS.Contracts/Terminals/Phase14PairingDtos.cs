using System;

namespace RetailPOS.Contracts.Terminals;

public enum PairingState
{
    PENDING,
    PAKE_VERIFIED,
    TMK_PROVISIONED,
    COMPLETED,
    EXPIRED,
    REVOKED
}

public sealed record CreatePairingSessionRequestDto(
    Guid TerminalId);

public sealed record CreatePairingSessionResponseDto(
    Guid PairingOperationId,
    string HumanPairingCode,
    string SaltHex);

public sealed record PairingChallengeResponseDto(

    Guid PairingOperationId,
    Guid StoreId,
    Guid TerminalId,
    string SaltHex);

public sealed record PairingInitiateRequestDto(
    Guid PairingOperationId,
    string ClientEphemeralABase64);

public sealed record PairingInitiateResponseDto(
    Guid PairingOperationId,
    string ServerEphemeralBBase64);

public sealed record PairingConfirmRequestDto(
    Guid PairingOperationId,
    string ClientEvidenceM1Base64,
    string SASConfirmationCode,
    string CandidateF1,
    string RetryAuthTokenBase64);

public sealed record EncryptedTmkDto(
    string IvBase64,
    string TagBase64,
    string CiphertextBase64,
    string AadBase64);

public sealed record PairingConfirmResponseDto(
    Guid PairingOperationId,
    string ServerEvidenceM2Base64,
    EncryptedTmkDto EncryptedTmk,
    string PublicKeySpkiBase64);

public sealed record PairingRetryRequestDto(
    Guid PairingOperationId,
    Guid StoreId,
    Guid TerminalId,
    string CandidateF1,
    string RetryAuthTokenBase64);

public sealed record PairingFinalizeRequestDto(
    Guid PairingOperationId,
    string FinalizeProofBase64);

public sealed record F2RotationArtifactDto(
    string Version,
    Guid StoreId,
    Guid TerminalId,
    Guid RotationOperationId,
    string NewCertificateFingerprint,
    string ClientNonceBase64,
    DateTime ExpiresAtUtc,
    string RootSignatureBase64,
    string? ConfirmationCode = null);

public sealed record CounterPairingTransactionState(
    Guid PairingOperationId,
    Guid StoreId,
    Guid TerminalId,
    string CandidateF1,
    string SharedKeyKpakeBase64,
    string ClientEvidenceM1Base64,
    string RetryAuthTokenBase64,
    string ClientEphemeralABase64,
    string ServerEphemeralBBase64,
    DateTime ExpiresAtUtc,
    string TxState);
