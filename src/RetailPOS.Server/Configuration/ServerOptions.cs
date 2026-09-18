using RetailPOS.Contracts.Common;

namespace RetailPOS.Server.Configuration;

/// <summary>
/// Strongly-typed production server configuration for network binding and directories.
/// Replaces environment variables (%ProgramData%) explicitly for Windows Service compatibility.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "ServerConfiguration";

    public int HttpPort { get; set; } = 5100;
    public int HttpsPort { get; set; } = 5101;
    public bool UseHttps { get; set; }
    public bool BindToAllInterfaces { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public string AllowedSubnet { get; set; } = "127.0.0.1/32";
    public string ServerId { get; set; } = "SRV-PRIMARY-01";
    public string StoreId { get; set; } = "STORE-001";
    public string StoreName { get; set; } = "Grozet Supermarket";
    public string DataDirectory { get; set; } = @"%ProgramData%\GROZET\RetailPOS\Database";
    public string LogsDirectory { get; set; } = @"%ProgramData%\GROZET\RetailPOS\Logs";
    public string BackupsDirectory { get; set; } = @"%ProgramData%\GROZET\RetailPOS\Backups";
    public string SecurityDirectory { get; set; } = @"%ProgramData%\GROZET\RetailPOS\Security";
    public string StagingDirectory { get; set; } = @"%ProgramData%\GROZET\RetailPOS\Staging";
    public string DatabaseFileName { get; set; } = RetailPOSPaths.DefaultDatabaseFileName;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public long MaxRequestBodySizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB

    public string ResolveDataDirectory()
    {
        var resolved = RetailPOSPaths.ResolvePath(DataDirectory);
        Directory.CreateDirectory(resolved);
        return resolved;
    }

    public string ResolveLogsDirectory()
    {
        var resolved = RetailPOSPaths.ResolvePath(LogsDirectory);
        Directory.CreateDirectory(resolved);
        return resolved;
    }

    public string ResolveBackupsDirectory()
    {
        var resolved = RetailPOSPaths.ResolvePath(BackupsDirectory);
        Directory.CreateDirectory(resolved);
        return resolved;
    }

    public string ResolveSecurityDirectory()
    {
        var resolved = RetailPOSPaths.ResolvePath(SecurityDirectory);
        Directory.CreateDirectory(resolved);
        return resolved;
    }

    public string ResolveStagingDirectory()
    {
        var resolved = RetailPOSPaths.ResolvePath(StagingDirectory);
        Directory.CreateDirectory(resolved);
        return resolved;
    }

    public void EnsureDirectoriesCreated()
    {
        ResolveDataDirectory();
        ResolveLogsDirectory();
        ResolveBackupsDirectory();
        ResolveSecurityDirectory();
        ResolveStagingDirectory();
    }

    public string ResolveDatabasePath()
    {
        return Path.Combine(ResolveDataDirectory(), DatabaseFileName);
    }

    public string BuildSqliteConnectionString()
    {
        return $"Data Source={ResolveDatabasePath()};Cache=Shared";
    }
}
