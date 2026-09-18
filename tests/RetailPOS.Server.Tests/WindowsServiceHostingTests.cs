using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPOS.Contracts.Common;
using RetailPOS.Server.BackgroundServices;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class WindowsServiceHostingTests : IDisposable
{
    private readonly string _tempRoot;

    public WindowsServiceHostingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"retailpos_svc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void WindowsServiceDeploymentSpecification_ServiceConstants_AreExact()
    {
        Assert.Equal("GrozetPOSServer", WindowsServiceDeploymentSpecification.ServiceName);
        Assert.Equal("Grozet Retail POS Server", WindowsServiceDeploymentSpecification.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(WindowsServiceDeploymentSpecification.Description));
        Assert.Equal(@"NT SERVICE\GrozetPOSServer", WindowsServiceDeploymentSpecification.ServiceAccount);
        Assert.Equal("auto", WindowsServiceDeploymentSpecification.StartMode);
    }

    [Fact]
    public void WindowsServiceDeploymentSpecification_RecoveryPolicy_MatchesApprovedValues()
    {
        Assert.Equal(86400, WindowsServiceDeploymentSpecification.FailureResetPeriodSeconds);
        Assert.Equal(60000, WindowsServiceDeploymentSpecification.RestartDelayMilliseconds);
        Assert.Equal("restart/60000/restart/60000/restart/60000", WindowsServiceDeploymentSpecification.FailureActions);
    }

    [Fact]
    public void WindowsServiceDeploymentSpecification_FirewallRules_MeetApprovedPortsAndProfiles()
    {
        Assert.Equal(5100, WindowsServiceDeploymentSpecification.HttpPort);
        Assert.Equal(5101, WindowsServiceDeploymentSpecification.HttpsPort);
        Assert.Equal(5105, WindowsServiceDeploymentSpecification.DiscoveryPort);

        Assert.Equal("Grozet-POS-Server-HTTP-In", WindowsServiceDeploymentSpecification.HttpFirewallRuleName);
        Assert.Equal("Grozet-POS-Server-HTTPS-In", WindowsServiceDeploymentSpecification.HttpsFirewallRuleName);
        Assert.Equal("Grozet-POS-Server-Discovery-In", WindowsServiceDeploymentSpecification.DiscoveryFirewallRuleName);

        Assert.Contains("Private", WindowsServiceDeploymentSpecification.AllowedFirewallProfiles);
        Assert.Contains("Domain", WindowsServiceDeploymentSpecification.AllowedFirewallProfiles);
        Assert.DoesNotContain("Public", WindowsServiceDeploymentSpecification.AllowedFirewallProfiles);

        // Approval validator tests
        Assert.True(WindowsServiceDeploymentSpecification.IsApprovedRule("Grozet-POS-Server-HTTP-In", 5100, "TCP", "Private"));
        Assert.True(WindowsServiceDeploymentSpecification.IsApprovedRule("Grozet-POS-Server-HTTPS-In", 5101, "TCP", "Domain,Private"));
        Assert.True(WindowsServiceDeploymentSpecification.IsApprovedRule("Grozet-POS-Server-Discovery-In", 5105, "UDP", "Private"));

        // Reject invalid ports/protocols/profiles
        Assert.False(WindowsServiceDeploymentSpecification.IsApprovedRule("Grozet-POS-Server-HTTP-In", 8080, "TCP", "Private"));
        Assert.False(WindowsServiceDeploymentSpecification.IsApprovedRule("Grozet-POS-Server-HTTP-In", 5100, "UDP", "Private"));
        Assert.False(WindowsServiceDeploymentSpecification.IsApprovedRule("Grozet-POS-Server-HTTP-In", 5100, "TCP", "PublicOnly"));
        Assert.False(WindowsServiceDeploymentSpecification.IsApprovedRule("Unknown-Rule", 5100, "TCP", "Private"));
    }

    [Fact]
    public async Task ScheduledBackupBackgroundService_GracefulShutdown_RespondsToCancellation()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var logger = NullLogger<ScheduledBackupBackgroundService>.Instance;

        var backgroundService = new ScheduledBackupBackgroundService(provider, logger);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Immediately cancel to simulate instant shutdown

        // StartAsync should complete immediately without exception when cancelled
        var startTask = backgroundService.StartAsync(cts.Token);
        await startTask;

        var stopTask = backgroundService.StopAsync(CancellationToken.None);
        await stopTask;

        Assert.True(startTask.IsCompletedSuccessfully);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task UdpServerDiscoveryListener_GracefulShutdown_RespondsToCancellation()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var env = new TestHostEnvironment { EnvironmentName = "Testing" };
        var logger = NullLogger<UdpServerDiscoveryListener>.Instance;

        var listener = new UdpServerDiscoveryListener(scopeFactory, env, logger);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var startTask = listener.StartAsync(cts.Token);
        await startTask;

        var stopTask = listener.StopAsync(CancellationToken.None);
        await stopTask;

        Assert.True(startTask.IsCompletedSuccessfully);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void DeploymentScript_ExistsAndHasValidPath()
    {
        // Find repository root by searching upward
        var dir = AppContext.BaseDirectory;
        string? scriptPath = null;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, "scripts", "deployment", "Deploy-WindowsService.ps1");
            if (File.Exists(candidate))
            {
                scriptPath = candidate;
                break;
            }
            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        Assert.NotNull(scriptPath);
        Assert.True(File.Exists(scriptPath));

        var content = File.ReadAllText(scriptPath);
        Assert.Contains("GrozetPOSServer", content);
        Assert.Contains(@"NT SERVICE\GrozetPOSServer", content);
        Assert.Contains("5100", content);
        Assert.Contains("5101", content);
        Assert.Contains("5105", content);
    }

    [Fact]
    public void WindowsServiceDeploymentSpecification_NTFS_PermissionModel_ProtectsOperationalDirectories()
    {
        Assert.Equal("(OI)(CI)RX", WindowsServiceDeploymentSpecification.ProgramFilesRequiredPermissions);
        Assert.Equal("(OI)(CI)M", WindowsServiceDeploymentSpecification.OperationalDirectoryServicePermissions);
        Assert.Equal("(OI)(CI)F", WindowsServiceDeploymentSpecification.AdministratorPermissions);

        var protectedDirs = WindowsServiceDeploymentSpecification.OperationalDirectoriesToProtect;
        Assert.Contains(RetailPOSPaths.DatabaseSubdirectory, protectedDirs);
        Assert.Contains(RetailPOSPaths.BackupsSubdirectory, protectedDirs);
        Assert.Contains(RetailPOSPaths.SecuritySubdirectory, protectedDirs);
        Assert.Contains(RetailPOSPaths.LogsSubdirectory, protectedDirs);
        Assert.Contains(RetailPOSPaths.StagingSubdirectory, protectedDirs);

        foreach (var dir in protectedDirs)
        {
            Assert.False(WindowsServiceDeploymentSpecification.IsUsersAccessPermitted(dir),
                $"Directory '{dir}' must NOT permit ordinary Users access.");
        }

        Assert.False(string.IsNullOrWhiteSpace(WindowsServiceDeploymentSpecification.DiagnosticLogsAccessModel));
        Assert.Contains("restricted", WindowsServiceDeploymentSpecification.DiagnosticLogsAccessModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Users has no access", WindowsServiceDeploymentSpecification.DiagnosticLogsAccessModel);
    }

    [Fact]
    public void DeploymentScript_NTFS_AclCommands_EnforceInheritanceRemovalAndLeastPrivilege()
    {
        var dir = AppContext.BaseDirectory;
        string? scriptPath = null;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, "scripts", "deployment", "Deploy-WindowsService.ps1");
            if (File.Exists(candidate))
            {
                scriptPath = candidate;
                break;
            }
            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        Assert.NotNull(scriptPath);
        var content = File.ReadAllText(scriptPath);

        // Verify inheritance removal commands
        Assert.Contains(@"icacls.exe ""$DataRoot"" /inheritance:r", content);
        Assert.Contains(@"icacls.exe ""$subPath"" /inheritance:r", content);

        // Verify administrative Full Control
        Assert.Contains(@"NT AUTHORITY\SYSTEM:(OI)(CI)F", content);
        Assert.Contains(@"BUILTIN\Administrators:(OI)(CI)F", content);

        // Verify Service Account Modify on operational directories and Read&Execute on Program Files
        Assert.Contains(@"${ServiceAccount}:(OI)(CI)M", content);
        Assert.Contains(@"${ServiceAccount}:(OI)(CI)RX", content);

        // Verify Users receives strictly no write or modify permissions anywhere in operational directories
        Assert.DoesNotContain(@"""Users:(OI)(CI)M""", content);
        Assert.DoesNotContain(@"""BUILTIN\Users:(OI)(CI)M""", content);
        Assert.DoesNotContain(@"""Users:(OI)(CI)F""", content);
        Assert.DoesNotContain(@"""BUILTIN\Users:(OI)(CI)F""", content);
    }

    [Fact]
    public void NTFS_OperationalDirectory_AclApplication_VerifiesNoUsersAccess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var testDir = Path.Combine(Path.GetTempPath(), $"grozet_acl_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);

        try
        {
            var currentUserName = Environment.UserName;

            // 1. Apply inheritance removal and least-privilege grants (simulating service account with current test user)
            RunIcacls($"\"{testDir}\" /inheritance:r");
            RunIcacls($"\"{testDir}\" /grant \"NT AUTHORITY\\SYSTEM:(OI)(CI)F\" \"BUILTIN\\Administrators:(OI)(CI)F\" \"{currentUserName}:(OI)(CI)M\" /Q");

            // 2. Query parent directory ACL
            var parentOutput = RunIcacls($"\"{testDir}\"");

            Assert.Contains("SYSTEM:(OI)(CI)(F)", parentOutput);
            Assert.Contains("Administrators:(OI)(CI)(F)", parentOutput);
            Assert.Contains($"{currentUserName}:(OI)(CI)(M)", parentOutput);

            // Crucial: BUILTIN\Users or Users must NOT be present
            Assert.DoesNotContain("BUILTIN\\Users", parentOutput);
            Assert.DoesNotContain("Users:(", parentOutput);

            // Inherited flags (I) must NOT be present on root because inheritance was stripped
            Assert.DoesNotContain("(I)(OI)(CI)", parentOutput);

            // 3. Create child directory and child file to verify child inheritance
            var childDir = Path.Combine(testDir, "SubDir");
            Directory.CreateDirectory(childDir);
            var childFile = Path.Combine(childDir, "child.txt");
            File.WriteAllText(childFile, "Operational payload");

            var childDirOutput = RunIcacls($"\"{childDir}\"");
            Assert.Contains("(I)(OI)(CI)(F)", childDirOutput);
            Assert.Contains("(I)(OI)(CI)(M)", childDirOutput);
            Assert.DoesNotContain("BUILTIN\\Users", childDirOutput);

            var childFileOutput = RunIcacls($"\"{childFile}\"");
            Assert.Contains("(I)(F)", childFileOutput);
            Assert.Contains("(I)(M)", childFileOutput);
            Assert.DoesNotContain("BUILTIN\\Users", childFileOutput);
        }
        finally
        {
            // Reset permissions to ensure clean deletion
            RunIcacls($"\"{testDir}\" /grant:r \"{Environment.UserName}:(OI)(CI)F\" /T /Q");
            try
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private static string RunIcacls(string arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "RetailPOS.Server";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
