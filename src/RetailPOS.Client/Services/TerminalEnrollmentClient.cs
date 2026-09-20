using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;

namespace RetailPOS.Client.Services;

public sealed class TerminalEnrollmentClient
{
    private readonly HttpClient _httpClient;
    private readonly string _tokenStoragePath;
    private static readonly byte[] Entropy = "GrozetPOS_Terminal_Entropy_Salt"u8.ToArray();

    public TerminalEnrollmentClient(HttpClient httpClient, string? tokenStoragePath = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenStoragePath = tokenStoragePath ?? Path.Combine(
            RetailPOSPaths.GetDefaultClientSecurityDirectory(),
            "terminal_token.dat");
    }

    public async Task<TerminalEnrollResponseDto> EnrollAsync(
        string serverBaseUrl,
        TerminalEnrollRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{serverBaseUrl.TrimEnd('/')}/api/v1/terminals/enroll";
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        reqMsg.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientProtocolVersionHeader, ProtocolVersionConstants.CurrentProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        reqMsg.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientVersionHeader, "1.0.0");
        var response = await _httpClient.SendAsync(reqMsg, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TerminalEnrollResponseDto>(cancellationToken: cancellationToken);
        if (result == null)
            throw new InvalidOperationException("TERMINAL_ENROLL_RESPONSE_EMPTY");

        SavePairingToken(result.TerminalAuthToken);
        return result;
    }

    public async Task<TerminalEnrollResponseDto> RepairAsync(
        string serverBaseUrl,
        TerminalRepairRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = $"{serverBaseUrl.TrimEnd('/')}/api/v1/terminals/repair";
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        reqMsg.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientProtocolVersionHeader, ProtocolVersionConstants.CurrentProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        reqMsg.Headers.TryAddWithoutValidation(ProtocolVersionConstants.ClientVersionHeader, "1.0.0");
        var response = await _httpClient.SendAsync(reqMsg, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TerminalEnrollResponseDto>(cancellationToken: cancellationToken);
        if (result == null)
            throw new InvalidOperationException("TERMINAL_REPAIR_RESPONSE_EMPTY");

        SavePairingToken(result.TerminalAuthToken);
        return result;
    }

    public string? LoadPairingToken()
    {
        if (!File.Exists(_tokenStoragePath))
            return null;

        try
        {
            var encrypted = File.ReadAllBytes(_tokenStoragePath);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            return Encoding.UTF8.GetString(encrypted);
        }
        catch
        {
            return null;
        }
    }

    public void SavePairingToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token cannot be empty.", nameof(token));

        var dir = Path.GetDirectoryName(_tokenStoragePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        byte[] payload;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            payload = ProtectedData.Protect(tokenBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        else
        {
            payload = tokenBytes;
        }

        File.WriteAllBytes(_tokenStoragePath, payload);
        ApplyRestrictiveAcls(_tokenStoragePath);
    }

    public void ClearPairingToken()
    {
        if (File.Exists(_tokenStoragePath))
            File.Delete(_tokenStoragePath);
    }

    private static void ApplyRestrictiveAcls(string filePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = new FileSecurity();

            // Disable inheritance and strip inherited rules
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var currentUser = WindowsIdentity.GetCurrent().User;

            fileSecurity.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, AccessControlType.Allow));

            if (currentUser != null && !currentUser.Equals(systemSid) && !currentUser.Equals(adminSid))
            {
                fileSecurity.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            fileInfo.SetAccessControl(fileSecurity);
        }
        catch
        {
            // Best effort on non-administrative user contexts
        }
    }
}
