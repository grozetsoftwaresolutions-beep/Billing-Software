namespace RetailPOS.Contracts.Common;

/// <summary>
/// Authoritative specification for Windows Service hosting, virtual service account identity,
/// failure recovery policy, NTFS ACL requirements, and Windows Firewall rules for Grozet POS.
/// Preserves Architecture v5.2.9.6 and Sub-Phase 16.3 security invariants.
/// </summary>
public static class WindowsServiceDeploymentSpecification
{
    /// <summary>
    /// The canonical Windows Service name recognized by Windows Service Control Manager (SCM).
    /// </summary>
    public const string ServiceName = "GrozetPOSServer";

    /// <summary>
    /// The user-visible display name shown in services.msc.
    /// </summary>
    public const string DisplayName = "Grozet Retail POS Server";

    /// <summary>
    /// Service description documenting authoritative local server responsibility.
    /// </summary>
    public const string Description = "Authoritative local server for Grozet Retail POS billing, inventory, and counter terminal synchronization.";

    /// <summary>
    /// The virtual service account identity under which the service must run.
    /// Virtual service accounts are auto-provisioned by Windows SCM, require no passwords,
    /// and operate under least privilege (no local Administrator rights).
    /// </summary>
    public const string ServiceAccount = @"NT SERVICE\GrozetPOSServer";

    /// <summary>
    /// Service startup configuration: automatic on system boot.
    /// </summary>
    public const string StartMode = "auto";

    /// <summary>
    /// Time period in seconds (86,400 = 24 hours) after which the SCM failure count resets to zero.
    /// </summary>
    public const int FailureResetPeriodSeconds = 86400;

    /// <summary>
    /// Delay in milliseconds (60,000 = 60 seconds) before restarting after an unexpected failure.
    /// </summary>
    public const int RestartDelayMilliseconds = 60000;

    /// <summary>
    /// SCM failure action command string: restart after 60s for 1st, 2nd, and subsequent crashes.
    /// </summary>
    public const string FailureActions = "restart/60000/restart/60000/restart/60000";

    /// <summary>
    /// Authoritative HTTP API port for terminal communication on local LAN.
    /// </summary>
    public const int HttpPort = 5100;

    /// <summary>
    /// Authoritative HTTPS API port for TLS-encrypted terminal communication.
    /// </summary>
    public const int HttpsPort = 5101;

    /// <summary>
    /// UDP broadcast/multicast port for local counter server discovery.
    /// </summary>
    public const int DiscoveryPort = 5105;

    /// <summary>
    /// Firewall rule name for inbound HTTP API traffic.
    /// </summary>
    public const string HttpFirewallRuleName = "Grozet-POS-Server-HTTP-In";

    /// <summary>
    /// Firewall rule name for inbound HTTPS API traffic.
    /// </summary>
    public const string HttpsFirewallRuleName = "Grozet-POS-Server-HTTPS-In";

    /// <summary>
    /// Firewall rule name for inbound UDP discovery traffic.
    /// </summary>
    public const string DiscoveryFirewallRuleName = "Grozet-POS-Server-Discovery-In";

    /// <summary>
    /// Scoped Windows Firewall profiles: Domain and Private only (public profile strictly blocked).
    /// </summary>
    public const string AllowedFirewallProfiles = "Domain,Private";

    /// <summary>
    /// Required NTFS permissions for application binaries under Program Files: Read &amp; Execute only.
    /// Strictly forbids Write, Modify, Delete, or Full Control to prevent binary tampering.
    /// </summary>
    public const string ProgramFilesRequiredPermissions = "(OI)(CI)RX";

    /// <summary>
    /// Required NTFS permissions for operational state directories under ProgramData: Modify.
    /// Grants read, write, create, and delete for data files without granting administrative ownership or DACL mutation.
    /// </summary>
    public const string OperationalDirectoryServicePermissions = "(OI)(CI)M";

    /// <summary>
    /// Administrative NTFS permissions required for SYSTEM and BUILTIN\Administrators: Full Control.
    /// </summary>
    public const string AdministratorPermissions = "(OI)(CI)F";

    /// <summary>
    /// Operational directories under %ProgramData%\GROZET\RetailPOS that must have inheritance stripped
    /// and access restricted exclusively to SYSTEM, BUILTIN\Administrators, and NT SERVICE\GrozetPOSServer.
    /// Generic Users (BUILTIN\Users) are strictly denied all access (zero ACEs granted).
    /// </summary>
    public static readonly string[] OperationalDirectoriesToProtect =
    [
        RetailPOSPaths.DatabaseSubdirectory,
        RetailPOSPaths.BackupsSubdirectory,
        RetailPOSPaths.SecuritySubdirectory,
        RetailPOSPaths.LogsSubdirectory,
        RetailPOSPaths.StagingSubdirectory
    ];

    /// <summary>
    /// Documents the diagnostic access model for the Logs directory:
    /// Accessible strictly to NT SERVICE\GrozetPOSServer (Modify to write rolling logs),
    /// BUILTIN\Administrators (Full Control for diagnostics and archiving), and NT AUTHORITY\SYSTEM (Full Control).
    /// Standard local users (BUILTIN\Users) are strictly denied read/traverse/write permissions
    /// to prevent diagnostic snooping (IPs, terminal identities, audit traces) or log tampering.
    /// </summary>
    public const string DiagnosticLogsAccessModel =
        "Logs directory is restricted to NT SERVICE\\GrozetPOSServer (Modify), BUILTIN\\Administrators (Full Control), and NT AUTHORITY\\SYSTEM (Full Control). BUILTIN\\Users has no access.";

    /// <summary>
    /// Validates whether ordinary unprivileged Users are permitted access to a specified directory.
    /// Returns false for all protected operational directories (Database, Backups, Security, Logs, Staging).
    /// </summary>
    public static bool IsUsersAccessPermitted(string directoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        foreach (var protectedDir in OperationalDirectoriesToProtect)
        {
            if (string.Equals(directoryName, protectedDir, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Validates whether a firewall rule request conforms to approved port, protocol, and profile specifications.
    /// </summary>
    public static bool IsApprovedRule(string ruleName, int port, string protocol, string profile)
    {
        if (ruleName == HttpFirewallRuleName && port == HttpPort &&
            string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
            profile.Contains("Private", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ruleName == HttpsFirewallRuleName && port == HttpsPort &&
            string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
            profile.Contains("Private", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ruleName == DiscoveryFirewallRuleName && port == DiscoveryPort &&
            string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase) &&
            profile.Contains("Private", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
