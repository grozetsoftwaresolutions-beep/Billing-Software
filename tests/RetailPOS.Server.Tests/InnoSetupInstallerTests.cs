using System;
using System.IO;
using System.Text.RegularExpressions;
using RetailPOS.Contracts.Common;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class InnoSetupInstallerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Specification_AppIds_AreDistinctAndDeterministic()
    {
        Assert.False(string.IsNullOrWhiteSpace(InnoSetupInstallerSpecification.ServerAppId));
        Assert.False(string.IsNullOrWhiteSpace(InnoSetupInstallerSpecification.ClientAppId));
        Assert.False(string.IsNullOrWhiteSpace(InnoSetupInstallerSpecification.UnifiedAppId));

        // Must be distinct
        Assert.NotEqual(InnoSetupInstallerSpecification.ServerAppId, InnoSetupInstallerSpecification.ClientAppId);
        Assert.NotEqual(InnoSetupInstallerSpecification.ServerAppId, InnoSetupInstallerSpecification.UnifiedAppId);
        Assert.NotEqual(InnoSetupInstallerSpecification.ClientAppId, InnoSetupInstallerSpecification.UnifiedAppId);

        // Exact approved GUIDs
        Assert.Equal("{{D37D15A2-3482-4E9A-963C-09B926F14920}", InnoSetupInstallerSpecification.ServerAppId);
        Assert.Equal("{{E5A4D3B1-5693-4F8B-A12C-18E2938B7401}", InnoSetupInstallerSpecification.ClientAppId);
        Assert.Equal("{{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}", InnoSetupInstallerSpecification.UnifiedAppId);
    }

    [Fact]
    public void Specification_Paths_MatchFilesystemArchitecture()
    {
        Assert.Equal(@"{autopf}\GROZET\RetailPOS\Server", InnoSetupInstallerSpecification.ServerDefaultDir);
        Assert.Equal(@"{autopf}\GROZET\RetailPOS\Client", InnoSetupInstallerSpecification.ClientDefaultDir);
        Assert.Equal(@"{autopf}\GROZET\RetailPOS", InnoSetupInstallerSpecification.UnifiedDefaultDir);

        Assert.Equal(@"{commonappdata}\GROZET\RetailPOS", InnoSetupInstallerSpecification.ServerDataRoot);
        Assert.Equal(@"{commonappdata}\GROZET\RetailPOS\Client", InnoSetupInstallerSpecification.ClientDataRoot);
    }

    [Fact]
    public void Specification_Ports_And_ServiceConstants_MatchWindowsServiceDeploymentSpecification()
    {
        Assert.Equal(WindowsServiceDeploymentSpecification.HttpPort, InnoSetupInstallerSpecification.HttpPort);
        Assert.Equal(WindowsServiceDeploymentSpecification.HttpsPort, InnoSetupInstallerSpecification.HttpsPort);
        Assert.Equal(WindowsServiceDeploymentSpecification.DiscoveryPort, InnoSetupInstallerSpecification.DiscoveryPort);

        Assert.Equal(WindowsServiceDeploymentSpecification.ServiceName, InnoSetupInstallerSpecification.ServiceName);
        Assert.Equal(WindowsServiceDeploymentSpecification.ServiceAccount, InnoSetupInstallerSpecification.ServiceAccount);

        Assert.Equal(5100, InnoSetupInstallerSpecification.HttpPort);
        Assert.Equal(5101, InnoSetupInstallerSpecification.HttpsPort);
        Assert.Equal(5105, InnoSetupInstallerSpecification.DiscoveryPort);
    }

    [Fact]
    public void PackagingFiles_AllExistOnDisk()
    {
        var commonIss = Path.Combine(RepoRoot, "packaging", "retailpos-common.iss");
        var serverIss = Path.Combine(RepoRoot, "packaging", "retailpos-server.iss");
        var clientIss = Path.Combine(RepoRoot, "packaging", "retailpos-client.iss");
        var unifiedIss = Path.Combine(RepoRoot, "packaging", "retailpos.iss");
        var buildScript = Path.Combine(RepoRoot, "scripts", "deployment", "Build-Installer.ps1");

        Assert.True(File.Exists(commonIss), $"Missing: {commonIss}");
        Assert.True(File.Exists(serverIss), $"Missing: {serverIss}");
        Assert.True(File.Exists(clientIss), $"Missing: {clientIss}");
        Assert.True(File.Exists(unifiedIss), $"Missing: {unifiedIss}");
        Assert.True(File.Exists(buildScript), $"Missing: {buildScript}");
    }

    [Fact]
    public void RetailPOSCommon_ContainsOnlySharedMetadata()
    {
        var commonIss = Path.Combine(RepoRoot, "packaging", "retailpos-common.iss");
        var content = File.ReadAllText(commonIss);

        // Must define shared metadata
        Assert.Contains(@"#define AppPublisher ""Grozet Software Solutions""", content);
        Assert.Contains(@"#define AppVersion ""1.0.0""", content);
        Assert.Contains(@"#define ServiceName ""GrozetPOSServer""", content);
        Assert.Contains(@"#define ServiceAccount ""NT SERVICE\GrozetPOSServer""", content);
        Assert.Contains(@"#define HttpPort ""5100""", content);
        Assert.Contains(@"#define HttpsPort ""5101""", content);
        Assert.Contains(@"#define DiscoveryPort ""5105""", content);

        // MUST NOT centralize role-specific sections or AppId in directives
        var normalized = content.Replace("\r", "");
        Assert.DoesNotContain("\n[Run]", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n[UninstallRun]", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n[UninstallDelete]", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\nDefaultDirName", normalized, StringComparison.OrdinalIgnoreCase);

        var nonCommentLines = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith(';') && !string.IsNullOrWhiteSpace(l));
        foreach (var line in nonCommentLines)
        {
            Assert.DoesNotContain("AppId", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ServerInstaller_Semantics_CompliesWithSpecification()
    {
        var serverIss = Path.Combine(RepoRoot, "packaging", "retailpos-server.iss");
        var content = File.ReadAllText(serverIss);

        Assert.True(
            InnoSetupInstallerSpecification.ValidateInstallerScriptContent(
                content,
                InnoSetupInstallerSpecification.ServerAppId,
                InnoSetupInstallerSpecification.ServerDefaultDir,
                out var violations),
            string.Join("; ", violations));

        Assert.Contains("OutputBaseFilename=GrozetRetailPOS_Server_Setup", content);
        Assert.Contains(@"#include ""retailpos-common.iss""", content);

        // Validates deployment script packaging
        Assert.Contains(@"Source: ""..\scripts\deployment\Deploy-WindowsService.ps1""; DestDir: ""{app}\scripts""", content);

        // Validates service installation and uninstallation hooks
        Assert.Contains("Deploy-WindowsService.ps1", content);
        Assert.Contains("-Action Install", content);
        Assert.Contains("-Action Uninstall", content);

        // Validates NO recursive deletion in directives
        var uninstallDirectives = InnoSetupInstallerSpecification.GetSectionDirectives(content, "UninstallDelete");
        Assert.NotEmpty(uninstallDirectives);
        foreach (var directive in uninstallDirectives)
        {
            Assert.DoesNotContain("filesandordirs", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{app}*", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{commonappdata}", directive, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dirifempty", directive, StringComparison.OrdinalIgnoreCase);
        }

        // Validates collision detection against Unified installer
        Assert.Contains(InnoSetupInstallerSpecification.UnifiedAppId.Trim('{', '}'), content);
    }

    [Fact]
    public void ClientInstaller_Semantics_CompliesWithSpecification()
    {
        var clientIss = Path.Combine(RepoRoot, "packaging", "retailpos-client.iss");
        var content = File.ReadAllText(clientIss);

        Assert.True(
            InnoSetupInstallerSpecification.ValidateInstallerScriptContent(
                content,
                InnoSetupInstallerSpecification.ClientAppId,
                InnoSetupInstallerSpecification.ClientDefaultDir,
                out var violations),
            string.Join("; ", violations));

        Assert.Contains("OutputBaseFilename=GrozetRetailPOS_Client_Setup", content);
        Assert.Contains(@"#include ""retailpos-common.iss""", content);

        // Client must NOT package Deploy-WindowsService.ps1 or register service
        Assert.DoesNotContain("Deploy-WindowsService.ps1", content);
        Assert.DoesNotContain("[UninstallRun]", content);

        // Validates shortcuts exist for POS Terminal only
        var iconsSection = InnoSetupInstallerSpecification.ExtractSection(content, "Icons");
        Assert.Contains("Grozet POS Terminal", iconsSection);
        Assert.DoesNotContain("powershell.exe", iconsSection, StringComparison.OrdinalIgnoreCase);

        // Validates NO recursive deletion in directives
        var uninstallDirectives = InnoSetupInstallerSpecification.GetSectionDirectives(content, "UninstallDelete");
        Assert.NotEmpty(uninstallDirectives);
        foreach (var directive in uninstallDirectives)
        {
            Assert.DoesNotContain("filesandordirs", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{app}*", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{commonappdata}", directive, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dirifempty", directive, StringComparison.OrdinalIgnoreCase);
        }

        // Validates collision detection against Unified installer
        Assert.Contains(InnoSetupInstallerSpecification.UnifiedAppId.Trim('{', '}'), content);
    }

    [Fact]
    public void UnifiedInstaller_Semantics_CompliesWithSpecification()
    {
        var unifiedIss = Path.Combine(RepoRoot, "packaging", "retailpos.iss");
        var content = File.ReadAllText(unifiedIss);

        Assert.True(
            InnoSetupInstallerSpecification.ValidateInstallerScriptContent(
                content,
                InnoSetupInstallerSpecification.UnifiedAppId,
                InnoSetupInstallerSpecification.UnifiedDefaultDir,
                out var violations),
            string.Join("; ", violations));

        Assert.Contains("OutputBaseFilename=GrozetRetailPOS_Setup", content);
        Assert.Contains(@"#include ""retailpos-common.iss""", content);

        // Validates role components
        var componentsSection = InnoSetupInstallerSpecification.ExtractSection(content, "Components");
        Assert.Contains(@"Name: ""server""", componentsSection);
        Assert.Contains(@"Name: ""client""", componentsSection);

        // Validates server payload in {app}\Server and client payload in {app}\Client
        Assert.Contains(@"DestDir: ""{app}\Server""", content);
        Assert.Contains(@"DestDir: ""{app}\Client""", content);

        // Validates NO recursive deletion in directives
        var uninstallDirectives = InnoSetupInstallerSpecification.GetSectionDirectives(content, "UninstallDelete");
        Assert.NotEmpty(uninstallDirectives);
        foreach (var directive in uninstallDirectives)
        {
            Assert.DoesNotContain("filesandordirs", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{app}*", directive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{commonappdata}", directive, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dirifempty", directive, StringComparison.OrdinalIgnoreCase);
        }

        // Validates collision detection against Server and Client standalone editions
        Assert.Contains(InnoSetupInstallerSpecification.ServerAppId.Trim('{', '}'), content);
        Assert.Contains(InnoSetupInstallerSpecification.ClientAppId.Trim('{', '}'), content);

        // Validates NextButtonClick component validation
        Assert.Contains("wpSelectComponents", content);
        Assert.Contains("IsComponentSelected", content);
    }

    [Fact]
    public void BuildScript_CompilerDiscovery_IncludesStandardLocationsAndDevFallback()
    {
        var buildScript = Path.Combine(RepoRoot, "scripts", "deployment", "Build-Installer.ps1");
        var content = File.ReadAllText(buildScript);

        // Checks PATH via Get-Command
        Assert.Contains("Get-Command iscc.exe", content);

        // Checks standard Program Files locations
        Assert.Contains("Inno Setup 6", content);

        // Checks development fallback with clear notice
        Assert.Contains("DEV-FALLBACK", content);

        // Compiles all three scripts
        Assert.Contains("retailpos-server.iss", content);
        Assert.Contains("retailpos-client.iss", content);
        Assert.Contains("retailpos.iss", content);

        // Reuses win-x64 publish profile
        Assert.Contains("/p:PublishProfile=win-x64", content);
    }

    [Fact]
    public void DeploymentScript_PackagedPath_MatchesInstallerExpectation()
    {
        var deployScript = Path.Combine(RepoRoot, "scripts", "deployment", "Deploy-WindowsService.ps1");
        Assert.True(File.Exists(deployScript), $"Deployment script must exist at: {deployScript}");

        // In packaging scripts, relative path is "..\scripts\deployment\Deploy-WindowsService.ps1"
        var packagingDir = Path.Combine(RepoRoot, "packaging");
        var relativeFromPackaging = Path.GetFullPath(Path.Combine(packagingDir, @"..\scripts\deployment\Deploy-WindowsService.ps1"));
        Assert.Equal(deployScript, relativeFromPackaging);
    }

    [Theory]
    [InlineData("packaging/retailpos-server.iss")]
    [InlineData("packaging/retailpos.iss")]
    public void InstallerScript_ContainsBoundedScmPolling(string scriptRelativePath)
    {
        var scriptPath = Path.Combine(RepoRoot, scriptRelativePath);
        Assert.True(File.Exists(scriptPath), $"Script file not found: {scriptPath}");

        var scriptContent = File.ReadAllText(scriptPath);

        // Must implement bounded SCM polling function
        Assert.Contains("function StopServerServiceWithBoundedPolling(): String;", scriptContent);

        // Must hook into PrepareToInstall
        Assert.Contains("function PrepareToInstall(var NeedsRestart: Boolean): String;", scriptContent);
        Assert.Contains("Result := StopServerServiceWithBoundedPolling();", scriptContent);

        // Must poll for STOPPED status
        Assert.Contains("STOPPED", scriptContent);

        // Must NOT use arbitrary sleep without polling loop (a single sleep after stop)
        // Ensure bounded loop with max iterations (e.g. 60 iterations with 500ms delay = 30s)
        Assert.Contains("for LoopCount := 1 to 60 do", scriptContent);

        // Must strictly preserve ProgramData in UninstallDelete
        Assert.DoesNotContain("Type: filesandordirs; Name: \"{commonappdata}", scriptContent);
        Assert.DoesNotContain("Type: dirifempty; Name: \"{commonappdata}", scriptContent);
    }
}
