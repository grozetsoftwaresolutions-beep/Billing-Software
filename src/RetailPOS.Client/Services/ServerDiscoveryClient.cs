using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetailPOS.Contracts.Discovery;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Security;

namespace RetailPOS.Client.Services;

public enum ClientSecurityState
{
    Unpaired = 0,
    PinnedNormal = 1,
    IdentityMismatchLocked = 2
}

public sealed class ServerDiscoveryClient
{
    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonCaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly string _pinnedIdentityPath;
    private ServerIdentityInfo? _pinnedIdentity;
    private bool _enforceHttps;

    public ClientSecurityState SecurityState { get; private set; } = ClientSecurityState.Unpaired;
    public ServerIdentityInfo? PinnedIdentity => _pinnedIdentity;
    public bool EnforceHttps => _enforceHttps;

    public ServerDiscoveryClient(HttpClient httpClient, string? pinnedIdentityPath = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _pinnedIdentityPath = pinnedIdentityPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GrozetPOS", "Client", "pinned_server.json");

        LoadPinnedIdentity();
    }

    public void LoadPinnedIdentity()
    {
        if (File.Exists(_pinnedIdentityPath))
        {
            try
            {
                var json = File.ReadAllText(_pinnedIdentityPath);
                var dto = JsonSerializer.Deserialize<ServerIdentityDto>(json, JsonCaseInsensitive);
                if (dto != null)
                {
                    _pinnedIdentity = new ServerIdentityInfo
                    {
                        ServerId = dto.ServerId,
                        StoreId = dto.StoreId,
                        ServerFingerprint = dto.ServerFingerprint,
                        PublicKeySpki = dto.PublicKeySpki,
                        CreatedAtUtc = dto.CreatedAtUtc
                    };
                    _enforceHttps = dto.EnforceHttps;
                    SecurityState = ClientSecurityState.PinnedNormal;
                    return;
                }
            }
            catch
            {
                // If corrupted or unreadable, remains unpaired
            }
        }

        _pinnedIdentity = null;
        SecurityState = ClientSecurityState.Unpaired;
    }

    public void SavePinnedIdentity(ServerIdentityInfo identity, bool enforceHttps = false)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var dir = Path.GetDirectoryName(_pinnedIdentityPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var dto = new ServerIdentityDto
        {
            ServerId = identity.ServerId,
            StoreId = identity.StoreId,
            ServerFingerprint = identity.ServerFingerprint,
            PublicKeySpki = identity.PublicKeySpki,
            CreatedAtUtc = identity.CreatedAtUtc,
            EnforceHttps = enforceHttps
        };

        var json = JsonSerializer.Serialize(dto, JsonIndented);
        File.WriteAllText(_pinnedIdentityPath, json);

        _pinnedIdentity = identity;
        _enforceHttps = enforceHttps;
        SecurityState = ClientSecurityState.PinnedNormal;
    }

    public void ClearPinnedIdentity()
    {
        if (File.Exists(_pinnedIdentityPath))
            File.Delete(_pinnedIdentityPath);

        _pinnedIdentity = null;
        SecurityState = ClientSecurityState.Unpaired;
    }

    public void TransitionToMismatchLocked()
    {
        SecurityState = ClientSecurityState.IdentityMismatchLocked;
    }

    public static string FormatHumanFingerprint(string hexFingerprint)
    {
        return ServerIdentityInfo.FormatHumanFingerprint(hexFingerprint);
    }

    public static async Task<ServerDiscoveryResponseDto?> DiscoverServerUdpAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        using var udpClient = new UdpClient();
        udpClient.EnableBroadcast = true;
        var probeBytes = Encoding.UTF8.GetBytes("GROZET_DISCOVERY_PROBE");
        var endpoint = new IPEndPoint(IPAddress.Broadcast, 5105);

        await udpClient.SendAsync(probeBytes, probeBytes.Length, endpoint);

        var receiveTask = udpClient.ReceiveAsync(cancellationToken).AsTask();
        var timeoutTask = Task.Delay(timeoutMs, cancellationToken);

        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
        if (completedTask == receiveTask)
        {
            var result = await receiveTask;
            var json = Encoding.UTF8.GetString(result.Buffer);
            return JsonSerializer.Deserialize<ServerDiscoveryResponseDto>(json, JsonCaseInsensitive);
        }

        return null;
    }

    public async Task<ServerChallengeResponseDto> SendChallengeAsync(
        string serverBaseUrl,
        string purpose = ChallengePurposes.ServerIdentityAuthentication,
        string? clientNonce = null,
        DateTimeOffset? clientTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        CheckHttpsDowngrade(serverBaseUrl);

        var nonce = clientNonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var timestamp = clientTimestamp ?? DateTimeOffset.UtcNow;
        var request = new ServerChallengeRequestDto(nonce, purpose, timestamp);

        var url = $"{serverBaseUrl.TrimEnd('/')}/api/v1/discovery/challenge";
        var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var challengeResponse = await response.Content.ReadFromJsonAsync<ServerChallengeResponseDto>(cancellationToken: cancellationToken);
        if (challengeResponse == null)
            throw new SecurityException("SERVER_IDENTITY_CHALLENGE_RESPONSE_EMPTY");

        return challengeResponse;
    }

    public bool VerifyChallengeResponse(
        ServerChallengeResponseDto response,
        string sentNonce,
        string expectedPurpose,
        DateTimeOffset clientTimestampUtc,
        string? expectedServerId = null,
        Guid? expectedStoreId = null,
        string? expectedFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        // 1. Nonce match
        if (!string.Equals(response.Nonce, sentNonce, StringComparison.Ordinal))
            throw new SecurityException("SERVER_IDENTITY_NONCE_MISMATCH");

        // 2. Purpose match
        if (!string.Equals(response.Purpose, expectedPurpose, StringComparison.Ordinal))
            throw new SecurityException("SERVER_IDENTITY_PURPOSE_MISMATCH");

        // 3. Expected ServerId match
        if (expectedServerId != null && !string.Equals(response.ServerId, expectedServerId, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("SERVER_IDENTITY_SERVER_ID_MISMATCH");

        // 4. Expected StoreId match
        if (expectedStoreId.HasValue && response.StoreId != expectedStoreId.Value)
            throw new SecurityException("SERVER_IDENTITY_STORE_ID_MISMATCH");

        // 5. PublicKeySpkiBase64 binding to ServerFingerprint
        var spkiBytes = Convert.FromBase64String(response.PublicKeySpkiBase64);
        var computedFingerprint = Convert.ToHexString(SHA256.HashData(spkiBytes));
        if (!string.Equals(response.ServerFingerprint, computedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("SERVER_IDENTITY_FINGERPRINT_HASH_MISMATCH");

        // 6. Expected fingerprint check
        if (expectedFingerprint != null && !string.Equals(response.ServerFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            TransitionToMismatchLocked();
            throw new SecurityException("SERVER_IDENTITY_FINGERPRINT_PIN_MISMATCH");
        }

        // 7. Verify Timestamp drift (within 60s)
        var drift = Math.Abs((DateTimeOffset.UtcNow - response.ServerTimestampUtc).TotalSeconds);
        if (drift > 60)
            throw new SecurityException("SERVER_IDENTITY_TIMESTAMP_DRIFT_EXCEEDED");

        // 8. Reconstruct 8-line canonical payload
        var canonicalPayload = ChallengeProtocolHelper.BuildCanonicalPayload(
            response.Purpose,
            response.Nonce,
            clientTimestampUtc,
            response.ServerId,
            response.StoreId,
            response.ServerFingerprint,
            computedFingerprint);

        // 9. Low-S and IEEE P1363 Signature verification
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(spkiBytes, out _);

        var signatureBytes = Convert.FromBase64String(response.SignatureBase64);
        var isValid = ChallengeProtocolHelper.VerifyCanonical(
            ecdsa,
            canonicalPayload,
            signatureBytes,
            out var errorCode,
            out var errorMessage);

        if (!isValid)
            throw new SecurityException($"SERVER_IDENTITY_SIGNATURE_INVALID: {errorCode} - {errorMessage}");

        return true;
    }

    public async Task<bool> ExecuteRePinningCeremonyAsync(
        string serverBaseUrl,
        RePinningRequestDto rePinningRequest,
        TerminalEnrollmentClient enrollmentClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rePinningRequest);
        ArgumentNullException.ThrowIfNull(enrollmentClient);
        CheckHttpsDowngrade(serverBaseUrl);

        var url = $"{serverBaseUrl.TrimEnd('/')}/api/v1/terminals/re-pin";
        var response = await _httpClient.PostAsJsonAsync(url, rePinningRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return false;

        var result = await response.Content.ReadFromJsonAsync<RePinningResponseDto>(cancellationToken: cancellationToken);
        if (result == null)
            return false;

        // Save updated terminal auth token
        enrollmentClient.SavePairingToken(result.NewTerminalAuthToken);

        // Update pinned identity
        SavePinnedIdentity(result.NewServerIdentity, _enforceHttps || serverBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        SecurityState = ClientSecurityState.PinnedNormal;
        return true;
    }

    public void CheckHttpsDowngrade(string requestedUrl)
    {
        if (_enforceHttps && requestedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("TRANSPORT_SECURITY_ERROR: Plain HTTP downgrade is rejected because HTTPS is enforced.");
        }
    }

    private sealed class ServerIdentityDto
    {
        public string ServerId { get; set; } = string.Empty;
        public Guid StoreId { get; set; }
        public string ServerFingerprint { get; set; } = string.Empty;
        public string PublicKeySpki { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public bool EnforceHttps { get; set; }
    }
}
