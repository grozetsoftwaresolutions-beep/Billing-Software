namespace RetailPOS.Domain.Tests;

using System;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

public class Phase12DomainTests
{
    [Fact]
    public void TerminalStatus_EnumValues_MatchSpecification()
    {
        Assert.Equal(0, (int)TerminalStatus.PendingApproval);
        Assert.Equal(1, (int)TerminalStatus.Active);
        Assert.Equal(2, (int)TerminalStatus.Inactive);
        Assert.Equal(3, (int)TerminalStatus.Decommissioned);
    }

    [Fact]
    public void EnrollmentTokenPurpose_EnumValues_MatchSpecification()
    {
        Assert.Equal(0, (int)EnrollmentTokenPurpose.NewEnrollment);
        Assert.Equal(1, (int)EnrollmentTokenPurpose.ExistingTerminalRepair);
        Assert.Equal(2, (int)EnrollmentTokenPurpose.ServerIdentityRePinning);
    }

    [Fact]
    public void CounterConnectivityStatus_EnumValues_MatchSpecification()
    {
        Assert.Equal(0, (int)CounterConnectivityStatus.Online);
        Assert.Equal(1, (int)CounterConnectivityStatus.Disconnected);
        Assert.Equal(2, (int)CounterConnectivityStatus.Offline);
    }

    [Fact]
    public void ServerState_EnumValues_MatchSpecification()
    {
        Assert.Equal(0, (int)ServerState.Available);
        Assert.Equal(1, (int)ServerState.FirstTimeSetupRequired);
        Assert.Equal(2, (int)ServerState.IdentityRecoveryRequired);
        Assert.Equal(3, (int)ServerState.StartupIntegrityFailure);
    }

    [Fact]
    public void Terminal_Constructor_NormalizesIsActiveWithStatus()
    {
        var id = Guid.NewGuid();
        var activeTerm = new Terminal(id, "POS-01", "Counter 1", status: TerminalStatus.Active);
        Assert.True(activeTerm.IsActive);
        Assert.Equal(TerminalStatus.Active, activeTerm.Status);

        var inactiveTerm = new Terminal(id, "POS-02", "Counter 2", status: TerminalStatus.Inactive);
        Assert.False(inactiveTerm.IsActive);
        Assert.Equal(TerminalStatus.Inactive, inactiveTerm.Status);

        var pendingTerm = new Terminal(id, "POS-03", "Counter 3", status: TerminalStatus.PendingApproval);
        Assert.False(pendingTerm.IsActive);
        Assert.Equal(TerminalStatus.PendingApproval, pendingTerm.Status);
    }

    [Fact]
    public void Terminal_LifecycleTransitions_UpdateStatusAndIsActive()
    {
        var term = new Terminal(Guid.NewGuid(), "POS-01", "Counter 1", status: TerminalStatus.Active);
        Assert.True(term.IsActive);

        // Deactivate
        term.Deactivate();
        Assert.Equal(TerminalStatus.Inactive, term.Status);
        Assert.False(term.IsActive);

        // Activate
        term.Activate();
        Assert.Equal(TerminalStatus.Active, term.Status);
        Assert.True(term.IsActive);

        // Enroll
        term.Deactivate();
        term.Enroll("HASH_TOKEN_123", "COUNTER_PC_01", "192.168.1.101");
        Assert.Equal(TerminalStatus.Active, term.Status);
        Assert.True(term.IsActive);
        Assert.Equal("HASH_TOKEN_123", term.PairingTokenHash);
        Assert.Equal("COUNTER_PC_01", term.MachineName);
        Assert.Equal("192.168.1.101", term.ClientIpAddress);
        Assert.NotNull(term.LastHeartbeatUtc);

        // Record Heartbeat
        var hbTime = DateTime.UtcNow;
        term.RecordHeartbeat("192.168.1.102", hbTime);
        Assert.Equal(hbTime, term.LastHeartbeatUtc);
        Assert.Equal("192.168.1.102", term.ClientIpAddress);

        // Rotate pairing token
        term.RotatePairingToken("HASH_TOKEN_456");
        Assert.Equal("HASH_TOKEN_456", term.PairingTokenHash);
    }

    [Fact]
    public void Terminal_Decommission_PermanentlyLocksTerminal_Irreversible()
    {
        var term = new Terminal(Guid.NewGuid(), "POS-01", "Counter 1", status: TerminalStatus.Active, pairingTokenHash: "EXISTING_HASH");
        Assert.True(term.IsActive);

        // Decommission terminal
        term.Decommission();
        Assert.Equal(TerminalStatus.Decommissioned, term.Status);
        Assert.False(term.IsActive);
        Assert.Null(term.PairingTokenHash);

        // Re-decommissioning throws
        var exDecom = Assert.Throws<DomainException>(() => term.Decommission());
        Assert.Equal("TERMINAL_ALREADY_DECOMMISSIONED", exDecom.ErrorCode);

        // Any modification attempt throws TERMINAL_DECOMMISSIONED
        var exAct = Assert.Throws<DomainException>(() => term.Activate());
        Assert.Equal("TERMINAL_DECOMMISSIONED", exAct.ErrorCode);

        var exDeact = Assert.Throws<DomainException>(() => term.Deactivate());
        Assert.Equal("TERMINAL_DECOMMISSIONED", exDeact.ErrorCode);

        var exEnroll = Assert.Throws<DomainException>(() => term.Enroll("NEW_HASH", "PC", "1.1.1.1"));
        Assert.Equal("TERMINAL_DECOMMISSIONED", exEnroll.ErrorCode);

        var exHb = Assert.Throws<DomainException>(() => term.RecordHeartbeat("1.1.1.1", DateTime.UtcNow));
        Assert.Equal("TERMINAL_DECOMMISSIONED", exHb.ErrorCode);

        var exRot = Assert.Throws<DomainException>(() => term.RotatePairingToken("NEW_HASH"));
        Assert.Equal("TERMINAL_DECOMMISSIONED", exRot.ErrorCode);
    }

    [Fact]
    public void Terminal_EmptyTerminalCodeOrTokenHash_ThrowsValidation()
    {
        Assert.Throws<DomainException>(() => new Terminal(Guid.NewGuid(), "", "Name"));
        Assert.Throws<DomainException>(() => new Terminal(Guid.NewGuid(), "   ", "Name"));

        var term = new Terminal(Guid.NewGuid(), "POS-01", "Counter 1");
        Assert.Throws<DomainException>(() => term.Enroll("", "PC", null));
        Assert.Throws<DomainException>(() => term.RotatePairingToken(""));
    }

    [Fact]
    public void ServerIdentityInfo_HumanVerifiableFingerprint_FormatsCorrectly()
    {
        var rawHex = "8F2A4B9172C4D0A6AABBCCDDEEFF00112233445566778899AABBCCDDEEFF0011";
        var formatted = ServerIdentityInfo.FormatHumanFingerprint(rawHex);
        Assert.Equal("8F2A-4B91-72C4-D0A6", formatted);

        var info = new ServerIdentityInfo
        {
            ServerId = "SRV-01",
            StoreId = Guid.NewGuid(),
            ServerFingerprint = rawHex,
            PublicKeySpki = "BASE64KEY",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.Equal("8F2A-4B91-72C4-D0A6", info.HumanVerifiableFingerprint);
    }

    [Fact]
    public void TerminalEnrollmentToken_CreationAndValidation()
    {
        var id = Guid.NewGuid();
        var tokenHash = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        var createdBy = Guid.NewGuid().ToString();

        // 1. NewEnrollment Token (Must have null terminal binding)
        var newEnrollToken = new TerminalEnrollmentToken(
            id,
            tokenHash,
            EnrollmentTokenPurpose.NewEnrollment,
            boundTerminalId: null,
            boundServerId: null,
            boundServerFingerprint: null,
            expiresAt,
            createdBy);

        Assert.False(newEnrollToken.IsConsumed);
        Assert.False(newEnrollToken.IsRevoked);
        Assert.True(newEnrollToken.ExpiresAtUtc > DateTime.UtcNow);

        // 2. NewEnrollment with bound terminal throws
        Assert.Throws<DomainException>(() => new TerminalEnrollmentToken(
            Guid.NewGuid(),
            tokenHash,
            EnrollmentTokenPurpose.NewEnrollment,
            boundTerminalId: Guid.NewGuid(),
            boundServerId: null,
            boundServerFingerprint: null,
            expiresAt,
            createdBy));

        // 3. ExistingTerminalRepair without bound terminal throws
        Assert.Throws<DomainException>(() => new TerminalEnrollmentToken(
            Guid.NewGuid(),
            tokenHash,
            EnrollmentTokenPurpose.ExistingTerminalRepair,
            boundTerminalId: null,
            boundServerId: null,
            boundServerFingerprint: null,
            expiresAt,
            createdBy));

        // 4. ServerIdentityRePinning requires both bound terminal and bound server identity
        var termId = Guid.NewGuid();
        Assert.Throws<DomainException>(() => new TerminalEnrollmentToken(
            Guid.NewGuid(),
            tokenHash,
            EnrollmentTokenPurpose.ServerIdentityRePinning,
            boundTerminalId: termId,
            boundServerId: null,
            boundServerFingerprint: null,
            expiresAt,
            createdBy));

        var rePinToken = new TerminalEnrollmentToken(
            Guid.NewGuid(),
            tokenHash,
            EnrollmentTokenPurpose.ServerIdentityRePinning,
            boundTerminalId: termId,
            boundServerId: "SRV-01",
            boundServerFingerprint: "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
            expiresAt,
            createdBy);
        Assert.False(rePinToken.IsConsumed);
    }

    [Fact]
    public void TerminalEnrollmentToken_ConsumeAndRevoke_EnforcesStateTransitions()
    {
        var token = new TerminalEnrollmentToken(
            Guid.NewGuid(),
            "HASH123",
            EnrollmentTokenPurpose.NewEnrollment,
            null, null, null,
            DateTime.UtcNow.AddMinutes(15),
            "admin");

        Assert.False(token.IsConsumed);

        // Consume
        var now = DateTime.UtcNow;
        token.Consume(now);
        Assert.True(token.IsConsumed);
        Assert.Equal(now, token.ConsumedAtUtc);

        // Re-consume throws
        var exConsume = Assert.Throws<DomainException>(() => token.Consume(DateTime.UtcNow));
        Assert.Equal("ENROLLMENT_TOKEN_ALREADY_USED", exConsume.ErrorCode);

        // Revoke fresh token
        var token2 = new TerminalEnrollmentToken(
            Guid.NewGuid(),
            "HASH456",
            EnrollmentTokenPurpose.NewEnrollment,
            null, null, null,
            DateTime.UtcNow.AddMinutes(15),
            "admin");

        token2.Revoke(DateTime.UtcNow);
        Assert.True(token2.IsRevoked);
        Assert.NotNull(token2.RevokedAtUtc);

        // Re-revoke is idempotent
        token2.Revoke(DateTime.UtcNow);
        Assert.True(token2.IsRevoked);

        // Expired token throws on consume
        var expiredToken = new TerminalEnrollmentToken(
            Guid.NewGuid(),
            "HASH789",
            EnrollmentTokenPurpose.NewEnrollment,
            null, null, null,
            DateTime.UtcNow.AddMinutes(-5),
            "admin");

        var exExp = Assert.Throws<DomainException>(() => expiredToken.Consume(DateTime.UtcNow));
        Assert.Equal("ENROLLMENT_TOKEN_EXPIRED", exExp.ErrorCode);
    }
}
