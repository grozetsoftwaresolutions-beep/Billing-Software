using RetailPOS.Server.Configuration;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class ServerConfigurationTests
{
    [Fact]
    public void ServerOptions_DefaultValues_AreProductionSafe()
    {
        var options = new ServerOptions();

        Assert.Equal(5100, options.HttpPort);
        Assert.Equal(5101, options.HttpsPort);
        Assert.False(options.BindToAllInterfaces);
        Assert.False(options.UseHttps);
        Assert.Equal("127.0.0.1", options.ListenAddress);
        Assert.Equal("127.0.0.1/32", options.AllowedSubnet);
        Assert.Equal("SRV-PRIMARY-01", options.ServerId);
        Assert.Equal("STORE-001", options.StoreId);
        Assert.Equal("Grozet Supermarket", options.StoreName);
        Assert.Equal("grozet_pos.db", options.DatabaseFileName);
        Assert.Equal(30, options.RequestTimeoutSeconds);
        Assert.Equal(10 * 1024 * 1024, options.MaxRequestBodySizeBytes);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Database", options.DataDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Logs", options.LogsDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Backups", options.BackupsDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Security", options.SecurityDirectory);
        Assert.Equal(@"%ProgramData%\GROZET\RetailPOS\Staging", options.StagingDirectory);
    }

    [Fact]
    public void ServerOptions_ResolvePaths_ExpandsEnvironmentVariables()
    {
        var options = new ServerOptions
        {
            DataDirectory = "%TEMP%\\GrozetPOS_Test\\Data",
            LogsDirectory = "%TEMP%\\GrozetPOS_Test\\Logs",
            BackupsDirectory = "%TEMP%\\GrozetPOS_Test\\Backups",
            SecurityDirectory = "%TEMP%\\GrozetPOS_Test\\Security",
            StagingDirectory = "%TEMP%\\GrozetPOS_Test\\Staging"
        };

        var resolvedData = options.ResolveDataDirectory();
        var resolvedLogs = options.ResolveLogsDirectory();
        var resolvedBackups = options.ResolveBackupsDirectory();
        var resolvedSecurity = options.ResolveSecurityDirectory();
        var resolvedStaging = options.ResolveStagingDirectory();
        var resolvedDbPath = options.ResolveDatabasePath();

        Assert.False(resolvedData.Contains('%', StringComparison.Ordinal));
        Assert.False(resolvedLogs.Contains('%', StringComparison.Ordinal));
        Assert.False(resolvedBackups.Contains('%', StringComparison.Ordinal));
        Assert.False(resolvedSecurity.Contains('%', StringComparison.Ordinal));
        Assert.False(resolvedStaging.Contains('%', StringComparison.Ordinal));
        Assert.EndsWith("grozet_pos.db", resolvedDbPath, StringComparison.OrdinalIgnoreCase);

        Assert.True(Directory.Exists(resolvedData));
        Assert.True(Directory.Exists(resolvedLogs));
        Assert.True(Directory.Exists(resolvedBackups));
        Assert.True(Directory.Exists(resolvedSecurity));
        Assert.True(Directory.Exists(resolvedStaging));

        // Clean up
        try
        {
            var root = Path.GetDirectoryName(resolvedData);
            if (root != null && Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
            // Best effort cleanup in temp
        }
    }

    [Fact]
    public void ServerOptions_BuildSqliteConnectionString_ContainsSharedCache()
    {
        var options = new ServerOptions
        {
            DataDirectory = "%TEMP%\\GrozetPOS_ConnTest"
        };

        var connString = options.BuildSqliteConnectionString();

        Assert.Contains("Data Source=", connString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cache=Shared", connString, StringComparison.OrdinalIgnoreCase);
    }
}
