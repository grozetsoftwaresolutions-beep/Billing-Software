using System.Reflection;
using RetailPOS.Application.Services;
using RetailPOS.Server.BackgroundServices;
namespace RetailPOS.Server.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Discovery;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Security;
using RetailPOS.Server.Middleware;
using Xunit;

public class Phase12NetworkingAndDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public Phase12NetworkingAndDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"retailpos_p12_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ChallengeProtocol_ValidSigningAndVerification_SucceedsWithLowS()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pubSpki = ecdsa.ExportSubjectPublicKeyInfo();
        var pubSpkiBase64 = Convert.ToBase64String(pubSpki);
        var fingerprint = Convert.ToHexString(SHA256.HashData(pubSpki));

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var timestamp = DateTimeOffset.UtcNow;
        var storeId = Guid.NewGuid();

        var payload = ChallengeProtocolHelper.BuildCanonicalPayload(
            ChallengePurposes.ServerIdentityAuthentication,
            nonce,
            timestamp,
            "SRV-01",
            storeId,
            fingerprint,
            fingerprint);

        var signature = ChallengeProtocolHelper.SignCanonical(ecdsa, payload);
        Assert.Equal(64, signature.Length);

        // Verify low-S property: s <= n/2
        var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        var halfN = ChallengeProtocolHelper.P256CurveOrder / 2;
        Assert.True(s <= halfN, "Signature S-scalar must be normalized to lower half of curve order.");

        // Verification passes
        var isValid = ChallengeProtocolHelper.VerifyCanonical(ecdsa, payload, signature, out var errCode, out var errMsg);
        Assert.True(isValid);
        Assert.Null(errCode);
    }

    [Fact]
    public void ChallengeProtocol_HighS_Signature_StrictlyRejected()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pubSpki = ecdsa.ExportSubjectPublicKeyInfo();
        var fingerprint = Convert.ToHexString(SHA256.HashData(pubSpki));

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var timestamp = DateTimeOffset.UtcNow;
        var storeId = Guid.NewGuid();

        var payload = ChallengeProtocolHelper.BuildCanonicalPayload(
            ChallengePurposes.ServerIdentityAuthentication,
            nonce,
            timestamp,
            "SRV-01",
            storeId,
            fingerprint,
            fingerprint);

        var canonicalSig = ChallengeProtocolHelper.SignCanonical(ecdsa, payload);
        var highSSig = ChallengeProtocolHelper.MalleateToHighS(canonicalSig);

        // Verify high-S property: s > n/2
        var sHigh = new BigInteger(highSSig.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        var halfN = ChallengeProtocolHelper.P256CurveOrder / 2;
        Assert.True(sHigh > halfN, "Malleated S-scalar must be in upper half of curve order.");

        // Verification MUST be rejected
        var isValid = ChallengeProtocolHelper.VerifyCanonical(ecdsa, payload, highSSig, out var errCode, out var errMsg);
        Assert.False(isValid);
        Assert.Equal("SERVER_IDENTITY_SIGNATURE_NON_CANONICAL_HIGH_S", errCode);
    }

    [Fact]
    public void ChallengeProtocol_ReplayedNonce_Returns409Conflict()
    {
        var helper = new NonceReplayCache();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var timestamp = DateTimeOffset.UtcNow;

        // First attempt succeeds
        var firstValid = helper.TryValidateAndRecordNonce(nonce, timestamp, out var err1, out _);
        Assert.True(firstValid);
        Assert.Null(err1);

        // Immediate replay is rejected
        var secondValid = helper.TryValidateAndRecordNonce(nonce, timestamp, out var err2, out _);
        Assert.False(secondValid);
        Assert.Equal("REPLAYED_CHALLENGE_NONCE", err2);
    }

    [Fact]
    public void ChallengeProtocol_TimestampDriftExceeded_Rejected()
    {
        var helper = new NonceReplayCache();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        // 65 seconds in the past (> 60s tolerance)
        var oldTimestamp = DateTimeOffset.UtcNow.AddSeconds(-65);
        var valid = helper.TryValidateAndRecordNonce(nonce, oldTimestamp, out var err, out _);
        Assert.False(valid);
        Assert.Equal("STALE_CHALLENGE_TIMESTAMP", err);

        // 65 seconds in the future
        var futureTimestamp = DateTimeOffset.UtcNow.AddSeconds(65);
        var validFuture = helper.TryValidateAndRecordNonce(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), futureTimestamp, out var errF, out _);
        Assert.False(validFuture);
        Assert.Equal("STALE_CHALLENGE_TIMESTAMP", errF);
    }

    [Fact]
    public void ChallengeProtocol_TamperedFields_FailVerification()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pubSpki = ecdsa.ExportSubjectPublicKeyInfo();
        var fingerprint = Convert.ToHexString(SHA256.HashData(pubSpki));

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var timestamp = DateTimeOffset.UtcNow;
        var storeId = Guid.NewGuid();

        var originalPayload = ChallengeProtocolHelper.BuildCanonicalPayload(
            ChallengePurposes.ServerIdentityAuthentication,
            nonce,
            timestamp,
            "SRV-01",
            storeId,
            fingerprint,
            fingerprint);

        var signature = ChallengeProtocolHelper.SignCanonical(ecdsa, originalPayload);

        // Tamper purpose
        var tamperedPurposePayload = ChallengeProtocolHelper.BuildCanonicalPayload(
            ChallengePurposes.ServerIdentityRePinning,
            nonce,
            timestamp,
            "SRV-01",
            storeId,
            fingerprint,
            fingerprint);
        Assert.False(ChallengeProtocolHelper.VerifyCanonical(ecdsa, tamperedPurposePayload, signature, out _, out _));

        // Tamper nonce
        var tamperedNoncePayload = ChallengeProtocolHelper.BuildCanonicalPayload(
            ChallengePurposes.ServerIdentityAuthentication,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            timestamp,
            "SRV-01",
            storeId,
            fingerprint,
            fingerprint);
        Assert.False(ChallengeProtocolHelper.VerifyCanonical(ecdsa, tamperedNoncePayload, signature, out _, out _));
    }

    [Fact]
    public async Task LoopbackRecoveryMiddleware_RemoteIp_Returns403Forbidden()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/recovery/provision-replacement-identity");
        request.Headers.Add("X-Test-Remote-IP", "192.168.1.100");
        request.Content = JsonContent.Create(new ProvisionReplacementIdentityRequestDto("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"));

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("REMOTE_RECOVERY_PROVISIONING_FORBIDDEN", body.ErrorCode);
    }

    [Fact]
    public async Task LoopbackRecoveryMiddleware_EarlyBoundary_MalformedBodyFromRemoteIp_Returns403WithoutModelBindingError()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Send completely invalid/malformed JSON from a remote IP
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/recovery/provision-replacement-identity");
        request.Headers.Add("X-Test-Remote-IP", "192.168.1.200");
        request.Content = new StringContent("{ not valid json at all :::", Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        // Must return 403 Forbidden from middleware BEFORE model binding or JSON deserialization
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("REMOTE_RECOVERY_PROVISIONING_FORBIDDEN", body.ErrorCode);
    }

    [Fact]
    public void LoopbackRecoveryMiddleware_LoopbackAddressEvaluation()
    {
        Assert.True(LoopbackRecoveryMiddleware.IsLoopbackAddress(IPAddress.Loopback));
        Assert.True(LoopbackRecoveryMiddleware.IsLoopbackAddress(IPAddress.IPv6Loopback));
        Assert.True(LoopbackRecoveryMiddleware.IsLoopbackAddress(IPAddress.Parse("127.0.0.1")));
        Assert.True(LoopbackRecoveryMiddleware.IsLoopbackAddress(IPAddress.Parse("::1")));

        // IPv4-mapped IPv6 loopback: ::ffff:127.0.0.1
        var mappedLoopback = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(LoopbackRecoveryMiddleware.IsLoopbackAddress(mappedLoopback));

        // Non-loopback LAN and WAN
        Assert.False(LoopbackRecoveryMiddleware.IsLoopbackAddress(IPAddress.Parse("192.168.1.50")));
        Assert.False(LoopbackRecoveryMiddleware.IsLoopbackAddress(IPAddress.Parse("10.0.0.1")));
        Assert.False(LoopbackRecoveryMiddleware.IsLoopbackAddress(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public async Task ServerIdentityProvisioningLock_SerializesConcurrentLockRequests()
    {
        var lockInstance = new ServerIdentityProvisioningLock();
        var mutexName = $"Test_Provisioning_Lock_{Guid.NewGuid():N}";

        // Thread 1 acquires lock
        var handle1 = await lockInstance.AcquireAsync(_tempDir, mutexName, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(handle1);

        // Thread 2 attempts to acquire lock with short timeout (100ms) -> times out
        var thread2TimedOut = false;
        try
        {
            await using var handle2 = await lockInstance.AcquireAsync(_tempDir, mutexName, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or LockTimeoutException)
        {
            thread2TimedOut = true;
        }

        Assert.True(thread2TimedOut, "Concurrent lock acquisition should time out while held by another caller.");

        // Release handle 1
        await handle1.DisposeAsync();

        // Thread 2 can now acquire lock
        await using var handle3 = await lockInstance.AcquireAsync(_tempDir, mutexName, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(handle3);
    }

    [Fact]
    public void UdpDiscoveryPort_ServerDiscoveryListener_ConfiguredWithApprovedPort5105()
    {
        var field = typeof(UdpServerDiscoveryListener)
            .GetField("DiscoveryPort", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var port = (int)field.GetValue(null)!;
        Assert.Equal(5105, port);
    }

    [Fact]
    public void UdpDiscoveryPort_ClientDiscovery_MatchesServerPort5105()
    {
        var assemblyLocation = typeof(Phase12NetworkingAndDiscoveryTests).Assembly.Location;
        var dir = Path.GetDirectoryName(assemblyLocation)!;
        while (!File.Exists(Path.Combine(dir, "RetailPOS.sln")) && Directory.GetParent(dir) != null)
        {
            dir = Directory.GetParent(dir)!.FullName;
        }

        var clientDiscoveryFile = Path.Combine(dir, "src", "RetailPOS.Client", "Services", "ServerDiscoveryClient.cs");
        Assert.True(File.Exists(clientDiscoveryFile), $"Expected file {clientDiscoveryFile} to exist");

        var sourceCode = File.ReadAllText(clientDiscoveryFile);
        Assert.Contains("new IPEndPoint(IPAddress.Broadcast, 5105)", sourceCode);

        var serverListenerFile = Path.Combine(dir, "src", "RetailPOS.Server", "BackgroundServices", "UdpServerDiscoveryListener.cs");
        Assert.True(File.Exists(serverListenerFile), $"Expected file {serverListenerFile} to exist");
        var serverSource = File.ReadAllText(serverListenerFile);
        Assert.Contains("DiscoveryPort = 5105", serverSource);
    }

    [Fact]
    public void TerminalHeartbeat_ConnectivityThreshold_ExactBoundaries()
    {
        var tracker = new TerminalHeartbeatTracker(null!, null!);
        var terminalId = Guid.NewGuid();

        // 1. Fresh heartbeat (<= 30s) -> Online
        var onlineStatus = tracker.GetConnectivityStatus(terminalId, DateTime.UtcNow.AddSeconds(-10));
        Assert.Equal(CounterConnectivityStatus.Online, onlineStatus);

        // 2. Exact 30-second boundary (<= 30s) -> Online
        var exactOnlineStatus = tracker.GetConnectivityStatus(terminalId, DateTime.UtcNow.AddSeconds(-29));
        Assert.Equal(CounterConnectivityStatus.Online, exactOnlineStatus);

        // 3. Just over 30 seconds (> 30s and <= 90s) -> Disconnected
        var disconnectedJustOver30 = tracker.GetConnectivityStatus(terminalId, DateTime.UtcNow.AddSeconds(-32));
        Assert.Equal(CounterConnectivityStatus.Disconnected, disconnectedJustOver30);

        // 4. In the middle of disconnected range (e.g. 60s) -> Disconnected
        var disconnectedMid = tracker.GetConnectivityStatus(terminalId, DateTime.UtcNow.AddSeconds(-60));
        Assert.Equal(CounterConnectivityStatus.Disconnected, disconnectedMid);

        // 5. Exact 90-second boundary (<= 90s) -> Disconnected
        var exactDisconnected = tracker.GetConnectivityStatus(terminalId, DateTime.UtcNow.AddSeconds(-89));
        Assert.Equal(CounterConnectivityStatus.Disconnected, exactDisconnected);

        // 6. Just over 90 seconds (> 90s) -> Offline
        var offlineJustOver90 = tracker.GetConnectivityStatus(terminalId, DateTime.UtcNow.AddSeconds(-92));
        Assert.Equal(CounterConnectivityStatus.Offline, offlineJustOver90);

        // 7. Long offline (e.g. 5 minutes) -> Offline
        var offlineLong = tracker.GetConnectivityStatus(terminalId, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(CounterConnectivityStatus.Offline, offlineLong);

        // 8. Null heartbeat timestamp -> Offline
        var offlineNull = tracker.GetConnectivityStatus(terminalId, null);
        Assert.Equal(CounterConnectivityStatus.Offline, offlineNull);
    }
}
