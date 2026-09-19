using System;
using System.Collections.Generic;

namespace RetailPOS.Contracts.Common;

/// <summary>
/// Authoritative specification for Inno Setup production installer packaging.
/// Defines frozen installer identities, versioning, role configurations, target filesystem locations,
/// service/firewall integration parameters, and customer-data preservation invariants.
/// Complies with Architecture v5.2.9.6, Sub-Phase 16.2 filesystem layout, Sub-Phase 16.3 Windows Service hosting,
/// and Sub-Phase 16.4 installer packaging standards.
/// </summary>
public static class InnoSetupInstallerSpecification
{
    /// <summary>
    /// Preserved deterministic Application GUID for the Dedicated Server installer.
    /// Preserved from Phase 14 official product identity.
    /// </summary>
    public const string ServerAppId = "{{D37D15A2-3482-4E9A-963C-09B926F14920}";

    /// <summary>
    /// Deterministic Application GUID for the Dedicated Client installer.
    /// </summary>
    public const string ClientAppId = "{{E5A4D3B1-5693-4F8B-A12C-18E2938B7401}";

    /// <summary>
    /// Deterministic Application GUID for the Unified Single-Station installer.
    /// </summary>
    public const string UnifiedAppId = "{{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}";

    /// <summary>
    /// Legal software publisher name.
    /// </summary>
    public const string AppPublisher = "Grozet Software Solutions";

    /// <summary>
    /// Frozen application semantic version.
    /// </summary>
    public const string AppVersion = "1.0.0";

    /// <summary>
    /// Frozen assembly and file version.
    /// </summary>
    public const string FileVersion = "1.0.0.0";

    /// <summary>
    /// Primary server binary name.
    /// </summary>
    public const string ServerExeName = "RetailPOS.Server.exe";

    /// <summary>
    /// Primary client desktop UI binary name.
    /// </summary>
    public const string ClientExeName = "RetailPOS.Client.exe";

    /// <summary>
    /// Server installer default directory under Program Files.
    /// Resolves to C:\Program Files\GROZET\RetailPOS\Server.
    /// </summary>
    public const string ServerDefaultDir = @"{autopf}\GROZET\RetailPOS\Server";

    /// <summary>
    /// Client installer default directory under Program Files.
    /// Resolves to C:\Program Files\GROZET\RetailPOS\Client.
    /// </summary>
    public const string ClientDefaultDir = @"{autopf}\GROZET\RetailPOS\Client";

    /// <summary>
    /// Unified installer default root installation directory under Program Files.
    /// Resolves to C:\Program Files\GROZET\RetailPOS.
    /// </summary>
    public const string UnifiedDefaultDir = @"{autopf}\GROZET\RetailPOS";

    /// <summary>
    /// Server mutable data root under ProgramData.
    /// Resolves to %ProgramData%\GROZET\RetailPOS.
    /// </summary>
    public const string ServerDataRoot = @"{commonappdata}\GROZET\RetailPOS";

    /// <summary>
    /// Client mutable data root under ProgramData.
    /// Resolves to %ProgramData%\GROZET\RetailPOS\Client.
    /// </summary>
    public const string ClientDataRoot = @"{commonappdata}\GROZET\RetailPOS\Client";

    /// <summary>
    /// Canonical service name configured by installer.
    /// </summary>
    public const string ServiceName = WindowsServiceDeploymentSpecification.ServiceName;

    /// <summary>
    /// Virtual service account under which the service must run (least privilege).
    /// </summary>
    public const string ServiceAccount = WindowsServiceDeploymentSpecification.ServiceAccount;

    /// <summary>
    /// Approved HTTP port.
    /// </summary>
    public const int HttpPort = WindowsServiceDeploymentSpecification.HttpPort;

    /// <summary>
    /// Approved HTTPS port.
    /// </summary>
    public const int HttpsPort = WindowsServiceDeploymentSpecification.HttpsPort;

    /// <summary>
    /// Approved LAN Discovery UDP port.
    /// </summary>
    public const int DiscoveryPort = WindowsServiceDeploymentSpecification.DiscoveryPort;

    /// <summary>
    /// Output filenames for all three installers.
    /// </summary>
    public const string ServerOutputExe = "GrozetRetailPOS_Server_Setup.exe";
    public const string ClientOutputExe = "GrozetRetailPOS_Client_Setup.exe";
    public const string UnifiedOutputExe = "GrozetRetailPOS_Setup.exe";

    /// <summary>
    /// Obsolete, rejected network ports that must NEVER appear in production installers.
    /// </summary>
    public static readonly string[] RejectedObsoletePorts = ["5001", "8080", "1433", "3306"];

    /// <summary>
    /// Obsolete, rejected service accounts that violate least-privilege deployment.
    /// </summary>
    public static readonly string[] RejectedServiceAccounts = ["LocalSystem", "Administrator", "BUILTIN\\Administrators"];

    /// <summary>
    /// Obsolete, rejected filesystem paths from earlier phases that must NEVER appear in production installers.
    /// </summary>
    public static readonly string[] RejectedDataPaths =
    [
        @"{commonappdata}\RetailPOS",
        @"%ProgramData%\GrozetPOS",
        @"{commonappdata}\GrozetPOS"
    ];

    /// <summary>
    /// Validates whether the given script content conforms to the architectural safety invariants.
    /// </summary>
    public static bool ValidateInstallerScriptContent(string scriptContent, string expectedAppId, string expectedDefaultDir, out List<string> violations)
    {
        violations = new List<string>();

        if (string.IsNullOrWhiteSpace(scriptContent))
        {
            violations.Add("Installer script content is empty.");
            return false;
        }

        // 1. Mandatory Identity and Version checks
        if (!scriptContent.Contains($"AppId={expectedAppId}", StringComparison.OrdinalIgnoreCase))
            violations.Add($"Script does not contain expected AppId: {expectedAppId}");

        if (!scriptContent.Contains($"DefaultDirName={expectedDefaultDir}", StringComparison.OrdinalIgnoreCase))
            violations.Add($"Script does not define expected DefaultDirName: {expectedDefaultDir}");

        // 2. Reject Obsolete Ports
        foreach (var badPort in RejectedObsoletePorts)
        {
            if (scriptContent.Contains($"--port {badPort}", StringComparison.OrdinalIgnoreCase) ||
                scriptContent.Contains($"LocalPort {badPort}", StringComparison.OrdinalIgnoreCase) ||
                scriptContent.Contains($":{badPort}", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Script contains obsolete/unauthorized network port: {badPort}");
            }
        }

        // 3. Reject Obsolete Service Accounts
        foreach (var badAccount in RejectedServiceAccounts)
        {
            if (scriptContent.Contains($"obj= {badAccount}", StringComparison.OrdinalIgnoreCase) ||
                scriptContent.Contains($"obj={badAccount}", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Script configures obsolete/unauthorized service account: {badAccount}");
            }
        }

        // 4. Reject Obsolete Paths
        foreach (var badPath in RejectedDataPaths)
        {
            if (scriptContent.Contains(badPath, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Script references obsolete ProgramData path: {badPath}");
            }
        }

        // 5. Customer Data Protection: [UninstallDelete] must NEVER delete {commonappdata}
        var uninstallDirectives = GetSectionDirectives(scriptContent, "UninstallDelete");
        foreach (var directive in uninstallDirectives)
        {
            if (directive.Contains("{commonappdata}", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"CRITICAL SECURITY VIOLATION: [UninstallDelete] directive targets customer ProgramData: {directive}");
            }

            // Prohibit recursive directory wiping
            if (directive.Contains("filesandordirs", StringComparison.OrdinalIgnoreCase) ||
                directive.Contains("{app}*", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"OWNERSHIP VIOLATION: [UninstallDelete] directive contains recursive filesandordirs or wildcard wiping: {directive}");
            }
        }

        // 6. Prohibit administrative PowerShell shortcuts in [Icons]
        var iconDirectives = GetSectionDirectives(scriptContent, "Icons");
        foreach (var directive in iconDirectives)
        {
            if (directive.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                directive.Contains("Deploy-WindowsService.ps1", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"SHORTCUT VIOLATION: [Icons] exposes administrative PowerShell or deployment scripts to user: {directive}");
            }
        }

        return violations.Count == 0;
    }

    /// <summary>
    /// Extracts all non-comment, non-header directive lines from a specified Inno Setup section.
    /// </summary>
    public static List<string> GetSectionDirectives(string script, string sectionName)
    {
        var sectionText = ExtractSection(script, sectionName);
        var directives = new List<string>();
        if (string.IsNullOrEmpty(sectionText)) return directives;

        using var reader = new System.IO.StringReader(sectionText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('['))
                continue;

            directives.Add(trimmed);
        }

        return directives;
    }

    public static string ExtractSection(string script, string sectionName)
    {
        var header = $"[{sectionName}]";
        var startIndex = script.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0) return string.Empty;

        var nextSectionIndex = script.IndexOf("\n[", startIndex + header.Length, StringComparison.Ordinal);
        if (nextSectionIndex < 0)
        {
            return script.Substring(startIndex);
        }

        return script.Substring(startIndex, nextSectionIndex - startIndex);
    }
}
