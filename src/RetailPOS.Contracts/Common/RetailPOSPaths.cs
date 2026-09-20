namespace RetailPOS.Contracts.Common;

/// <summary>
/// Authoritative production filesystem layout constants and deterministic path resolvers.
/// Enforces complete architectural separation between immutable application binaries (Program Files)
/// and controlled mutable operational state (%ProgramData%\GROZET\RetailPOS\).
/// </summary>
public static class RetailPOSPaths
{
    // Production Server Defaults (%ProgramData%\GROZET\RetailPOS\)
    public const string DefaultServerDataRoot = @"%ProgramData%\GROZET\RetailPOS";
    public const string DatabaseSubdirectory = "Database";
    public const string BackupsSubdirectory = "Backups";
    public const string SecuritySubdirectory = "Security";
    public const string LogsSubdirectory = "Logs";
    public const string StagingSubdirectory = "Staging";
    public const string DefaultDatabaseFileName = "grozet_pos.db";

    // Production Client Defaults (%ProgramData%\GROZET\RetailPOS\Client\)
    public const string DefaultClientDataRoot = @"%ProgramData%\GROZET\RetailPOS\Client";
    public const string ClientConfigSubdirectory = "Config";
    public const string ClientSecuritySubdirectory = "Security";
    public const string ClientLogsSubdirectory = "Logs";

    // Installation Paths (Immutable binaries & resources in Program Files)
    public const string DefaultServerProgramFilesPath = @"%ProgramFiles%\GROZET\RetailPOS\Server";
    public const string DefaultClientProgramFilesPath = @"%ProgramFiles%\GROZET\RetailPOS\Client";

    /// <summary>
    /// Resolves an environment-variable path (e.g. %ProgramData%) to a normalized absolute path.
    /// </summary>
    public static string ResolvePath(string rawPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        return Path.GetFullPath(expanded);
    }

    // Server Path Resolvers
    public static string GetDefaultServerRoot() => ResolvePath(DefaultServerDataRoot);
    public static string GetDefaultDatabaseDirectory() => Path.Combine(GetDefaultServerRoot(), DatabaseSubdirectory);
    public static string GetDefaultBackupsDirectory() => Path.Combine(GetDefaultServerRoot(), BackupsSubdirectory);
    public static string GetDefaultSecurityDirectory() => Path.Combine(GetDefaultServerRoot(), SecuritySubdirectory);
    public static string GetDefaultLogsDirectory() => Path.Combine(GetDefaultServerRoot(), LogsSubdirectory);
    public static string GetDefaultStagingDirectory() => Path.Combine(GetDefaultServerRoot(), StagingSubdirectory);
    public static string GetDefaultDatabasePath() => Path.Combine(GetDefaultDatabaseDirectory(), DefaultDatabaseFileName);
    public static string GetDefaultMigrationStagingDirectory() => Path.Combine(GetDefaultStagingDirectory(), "Migration");
    public static string GetDefaultMigrationMarkerPath() => Path.Combine(GetDefaultMigrationStagingDirectory(), "migration_state.json");
    public static string GetDefaultMigrationJournalPath() => Path.Combine(GetDefaultLogsDirectory(), "migration_audit.journal");

    // Client Path Resolvers
    public static string GetDefaultClientRoot() => ResolvePath(DefaultClientDataRoot);
    public static string GetDefaultClientConfigDirectory() => Path.Combine(GetDefaultClientRoot(), ClientConfigSubdirectory);
    public static string GetDefaultClientSecurityDirectory() => Path.Combine(GetDefaultClientRoot(), ClientSecuritySubdirectory);
    public static string GetDefaultClientLogsDirectory() => Path.Combine(GetDefaultClientRoot(), ClientLogsSubdirectory);

    // Installation Path Resolvers
    public static string GetDefaultServerInstallationDirectory() => ResolvePath(DefaultServerProgramFilesPath);
    public static string GetDefaultClientInstallationDirectory() => ResolvePath(DefaultClientProgramFilesPath);

    /// <summary>
    /// Ensures that all standard server mutable directories exist under the specified root or default root.
    /// Does not apply speculative final Windows Service ACLs (deferred to Sub-Phase 16.3/16.4).
    /// </summary>
    public static void EnsureServerDirectoriesCreated(string? serverRoot = null)
    {
        var root = !string.IsNullOrWhiteSpace(serverRoot) ? ResolvePath(serverRoot) : GetDefaultServerRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, DatabaseSubdirectory));
        Directory.CreateDirectory(Path.Combine(root, BackupsSubdirectory));
        Directory.CreateDirectory(Path.Combine(root, SecuritySubdirectory));
        Directory.CreateDirectory(Path.Combine(root, LogsSubdirectory));
        Directory.CreateDirectory(Path.Combine(root, StagingSubdirectory));
        Directory.CreateDirectory(Path.Combine(root, StagingSubdirectory, "Migration"));
    }

    /// <summary>
    /// Ensures that all standard client mutable directories exist under the specified root or default root.
    /// </summary>
    public static void EnsureClientDirectoriesCreated(string? clientRoot = null)
    {
        var root = !string.IsNullOrWhiteSpace(clientRoot) ? ResolvePath(clientRoot) : GetDefaultClientRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ClientConfigSubdirectory));
        Directory.CreateDirectory(Path.Combine(root, ClientSecuritySubdirectory));
        Directory.CreateDirectory(Path.Combine(root, ClientLogsSubdirectory));
    }

    /// <summary>
    /// Validates that candidatePath resolves strictly within or equal to rootDirectory,
    /// rejecting directory-traversal attempts (e.g., "../") that escape the designated root.
    /// </summary>
    public static bool IsPathWithinRoot(string candidatePath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirectory))
            return false;

        var fullCandidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidatePath))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootDirectory))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
