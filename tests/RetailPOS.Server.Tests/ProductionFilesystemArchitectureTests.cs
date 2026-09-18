using System;
using System.IO;
using RetailPOS.Contracts.Common;
using RetailPOS.Server.Configuration;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class ProductionFilesystemArchitectureTests : IDisposable
{
    private readonly string _testRoot;

    public ProductionFilesystemArchitectureTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"retailpos_fs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup of temp test directories
        }
    }

    [Fact]
    public void ServerOptions_DefaultValues_PointToApprovedProgramDataLayout()
    {
        var options = new ServerOptions();

        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Database", options.DataDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Logs", options.LogsDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Backups", options.BackupsDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Security", options.SecurityDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Staging", options.StagingDirectory);
    }

    [Fact]
    public void RetailPOSPaths_Defaults_FollowApprovedSeparation()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var expectedServerRoot = Path.Combine(programData, "GROZET", "RetailPOS");
        var expectedClientRoot = Path.Combine(programData, "GROZET", "RetailPOS", "Client");

        Assert.Equal(expectedServerRoot, RetailPOSPaths.GetDefaultServerRoot());
        Assert.Equal(Path.Combine(expectedServerRoot, "Database"), RetailPOSPaths.GetDefaultDatabaseDirectory());
        Assert.Equal(Path.Combine(expectedServerRoot, "Backups"), RetailPOSPaths.GetDefaultBackupsDirectory());
        Assert.Equal(Path.Combine(expectedServerRoot, "Security"), RetailPOSPaths.GetDefaultSecurityDirectory());
        Assert.Equal(Path.Combine(expectedServerRoot, "Logs"), RetailPOSPaths.GetDefaultLogsDirectory());
        Assert.Equal(Path.Combine(expectedServerRoot, "Staging"), RetailPOSPaths.GetDefaultStagingDirectory());
        Assert.Equal(Path.Combine(expectedServerRoot, "Database", "grozet_pos.db"), RetailPOSPaths.GetDefaultDatabasePath());

        Assert.Equal(expectedClientRoot, RetailPOSPaths.GetDefaultClientRoot());
        Assert.Equal(Path.Combine(expectedClientRoot, "Config"), RetailPOSPaths.GetDefaultClientConfigDirectory());
        Assert.Equal(Path.Combine(expectedClientRoot, "Security"), RetailPOSPaths.GetDefaultClientSecurityDirectory());
        Assert.Equal(Path.Combine(expectedClientRoot, "Logs"), RetailPOSPaths.GetDefaultClientLogsDirectory());
    }

    [Fact]
    public void AllProductionPaths_ResolveOutsideProgramFiles()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var pathsToCheck = new[]
        {
            RetailPOSPaths.GetDefaultServerRoot(),
            RetailPOSPaths.GetDefaultDatabaseDirectory(),
            RetailPOSPaths.GetDefaultBackupsDirectory(),
            RetailPOSPaths.GetDefaultSecurityDirectory(),
            RetailPOSPaths.GetDefaultLogsDirectory(),
            RetailPOSPaths.GetDefaultStagingDirectory(),
            RetailPOSPaths.GetDefaultDatabasePath(),
            RetailPOSPaths.GetDefaultClientRoot(),
            RetailPOSPaths.GetDefaultClientConfigDirectory(),
            RetailPOSPaths.GetDefaultClientSecurityDirectory(),
            RetailPOSPaths.GetDefaultClientLogsDirectory()
        };

        foreach (var path in pathsToCheck)
        {
            Assert.False(
                path.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase),
                $"Path '{path}' unexpectedly resides within ProgramFiles: '{programFiles}'");

            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                Assert.False(
                    path.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase),
                    $"Path '{path}' unexpectedly resides within ProgramFilesX86: '{programFilesX86}'");
            }
        }
    }

    [Fact]
    public void ClientPaths_CompletelySeparatedFromServerDatabaseAndBackups()
    {
        var serverDb = RetailPOSPaths.GetDefaultDatabaseDirectory();
        var serverBackups = RetailPOSPaths.GetDefaultBackupsDirectory();
        var serverSecurity = RetailPOSPaths.GetDefaultSecurityDirectory();
        var serverStaging = RetailPOSPaths.GetDefaultStagingDirectory();

        var clientRoot = RetailPOSPaths.GetDefaultClientRoot();
        var clientSecurity = RetailPOSPaths.GetDefaultClientSecurityDirectory();
        var clientConfig = RetailPOSPaths.GetDefaultClientConfigDirectory();
        var clientLogs = RetailPOSPaths.GetDefaultClientLogsDirectory();

        Assert.NotEqual(serverDb, clientRoot);
        Assert.NotEqual(serverBackups, clientRoot);
        Assert.NotEqual(serverSecurity, clientSecurity);
        Assert.NotEqual(serverStaging, clientRoot);

        Assert.False(clientSecurity.StartsWith(serverDb, StringComparison.OrdinalIgnoreCase));
        Assert.False(clientSecurity.StartsWith(serverBackups, StringComparison.OrdinalIgnoreCase));
        Assert.False(clientConfig.StartsWith(serverDb, StringComparison.OrdinalIgnoreCase));
        Assert.False(clientLogs.StartsWith(serverBackups, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkingDirectoryIndependence_Verified()
    {
        var originalCwd = Environment.CurrentDirectory;
        var tempSubDir = Path.Combine(_testRoot, "cwd_isolation_test");
        Directory.CreateDirectory(tempSubDir);

        try
        {
            Environment.CurrentDirectory = tempSubDir;

            var options = new ServerOptions();
            var resolvedDbPath = options.ResolveDatabasePath();
            var resolvedLogs = options.ResolveLogsDirectory();
            var resolvedBackups = options.ResolveBackupsDirectory();
            var resolvedSecurity = options.ResolveSecurityDirectory();
            var resolvedStaging = options.ResolveStagingDirectory();

            Assert.False(resolvedDbPath.StartsWith(tempSubDir, StringComparison.OrdinalIgnoreCase));
            Assert.False(resolvedLogs.StartsWith(tempSubDir, StringComparison.OrdinalIgnoreCase));
            Assert.False(resolvedBackups.StartsWith(tempSubDir, StringComparison.OrdinalIgnoreCase));
            Assert.False(resolvedSecurity.StartsWith(tempSubDir, StringComparison.OrdinalIgnoreCase));
            Assert.False(resolvedStaging.StartsWith(tempSubDir, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(RetailPOSPaths.GetDefaultDatabasePath(), resolvedDbPath);
            Assert.Equal(RetailPOSPaths.GetDefaultLogsDirectory(), resolvedLogs);
            Assert.Equal(RetailPOSPaths.GetDefaultBackupsDirectory(), resolvedBackups);
            Assert.Equal(RetailPOSPaths.GetDefaultSecurityDirectory(), resolvedSecurity);
            Assert.Equal(RetailPOSPaths.GetDefaultStagingDirectory(), resolvedStaging);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Fact]
    public void PathTraversal_OutsideRoot_RejectedByValidator()
    {
        var approvedRoot = Path.Combine(_testRoot, "SafeRoot");
        Directory.CreateDirectory(approvedRoot);

        // Legitimate paths within root
        Assert.True(RetailPOSPaths.IsPathWithinRoot(Path.Combine(approvedRoot, "child"), approvedRoot));
        Assert.True(RetailPOSPaths.IsPathWithinRoot(Path.Combine(approvedRoot, "child", "grandchild", "file.db"), approvedRoot));
        Assert.True(RetailPOSPaths.IsPathWithinRoot(Path.Combine(approvedRoot, "child", "..", "child", "file.db"), approvedRoot));

        // Traversal attempts escaping root
        Assert.False(RetailPOSPaths.IsPathWithinRoot(Path.Combine(approvedRoot, "..", "escaped.db"), approvedRoot));
        Assert.False(RetailPOSPaths.IsPathWithinRoot(Path.Combine(approvedRoot, "..", "..", "Windows", "System32"), approvedRoot));
        Assert.False(RetailPOSPaths.IsPathWithinRoot(Path.Combine(approvedRoot, "sub", "..", "..", "outside"), approvedRoot));
        Assert.False(RetailPOSPaths.IsPathWithinRoot(@"C:\Totally\Different\Path", approvedRoot));
    }

    [Fact]
    public void EnsureDirectoriesCreated_CreatesDirectoriesWithoutSpeculativeAcls()
    {
        var customServerRoot = Path.Combine(_testRoot, "CustomServer");
        var customClientRoot = Path.Combine(_testRoot, "CustomClient");

        RetailPOSPaths.EnsureServerDirectoriesCreated(customServerRoot);
        RetailPOSPaths.EnsureClientDirectoriesCreated(customClientRoot);

        Assert.True(Directory.Exists(Path.Combine(customServerRoot, "Database")));
        Assert.True(Directory.Exists(Path.Combine(customServerRoot, "Backups")));
        Assert.True(Directory.Exists(Path.Combine(customServerRoot, "Security")));
        Assert.True(Directory.Exists(Path.Combine(customServerRoot, "Logs")));
        Assert.True(Directory.Exists(Path.Combine(customServerRoot, "Staging")));

        Assert.True(Directory.Exists(Path.Combine(customClientRoot, "Config")));
        Assert.True(Directory.Exists(Path.Combine(customClientRoot, "Security")));
        Assert.True(Directory.Exists(Path.Combine(customClientRoot, "Logs")));
    }

    [Fact]
    public void ProgramFiles_ImmutableBinariesRoot_IsDistinctFromDataRoot()
    {
        Assert.NotEqual(RetailPOSPaths.DefaultServerProgramFilesPath, RetailPOSPaths.DefaultServerDataRoot);
        Assert.NotEqual(RetailPOSPaths.DefaultClientProgramFilesPath, RetailPOSPaths.DefaultClientDataRoot);

        Assert.Contains("ProgramFiles", RetailPOSPaths.DefaultServerProgramFilesPath);
        Assert.Contains("ProgramFiles", RetailPOSPaths.DefaultClientProgramFilesPath);

        Assert.DoesNotContain("ProgramFiles", RetailPOSPaths.DefaultServerDataRoot);
        Assert.DoesNotContain("ProgramFiles", RetailPOSPaths.DefaultClientDataRoot);
    }
}
