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
