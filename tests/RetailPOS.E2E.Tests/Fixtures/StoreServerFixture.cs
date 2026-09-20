using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetailPOS.Application.Hardware;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Interceptors;

namespace RetailPOS.E2E.Tests.Fixtures;

/// <summary>
/// Authoritative isolated test fixture hosting an in-process ASP.NET Core Kestrel server
/// backed by an isolated, temporary SQLite database running in WAL mode with foreign keys.
/// Cleans up all temporary filesystem resources, file locks, and pooled connections deterministically.
/// </summary>
public class StoreServerFixture : WebApplicationFactory<Program>, IAsyncDisposable
{
    private readonly string _testId;
    private readonly string _dataDir;
    private readonly string _dbPath;
    private readonly string _logsDir;
    private readonly string _backupsDir;
    private readonly string _securityDir;
    private readonly string _stagingDir;
    private bool _disposed;

    public string DataDirectory => _dataDir;
    public string DatabasePath => _dbPath;
    public string LogsDirectory => _logsDir;
    public string BackupsDirectory => _backupsDir;
    public string SecurityDirectory => _securityDir;
    public string StagingDirectory => _stagingDir;
    public MockHardwareDeviceManager HardwareManager { get; }
    public bool PreserveDataDirectoryOnDispose { get; set; }

    public StoreServerFixture(string? existingDataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(existingDataDirectory) && Directory.Exists(existingDataDirectory))
        {
            _dataDir = existingDataDirectory;
            _testId = Path.GetFileName(existingDataDirectory);
        }
        else
        {
            _testId = Guid.NewGuid().ToString("N");
            _dataDir = Path.Combine(Path.GetTempPath(), $"retailpos_e2e_{_testId}");
        }

        _dbPath = Path.Combine(_dataDir, "pos.db");
        _logsDir = Path.Combine(_dataDir, "Logs");
        _backupsDir = Path.Combine(_dataDir, "Backups");
        _securityDir = Path.Combine(_dataDir, "Security");
        _stagingDir = Path.Combine(_dataDir, "Staging");

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_logsDir);
        Directory.CreateDirectory(_backupsDir);
        Directory.CreateDirectory(_securityDir);
        Directory.CreateDirectory(_stagingDir);

        HardwareManager = new MockHardwareDeviceManager();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ServerConfiguration:DataDirectory"] = _dataDir,
                ["ServerConfiguration:LogsDirectory"] = _logsDir,
                ["ServerConfiguration:BackupsDirectory"] = _backupsDir,
                ["ServerConfiguration:SecurityDirectory"] = _securityDir,
                ["ServerConfiguration:StagingDirectory"] = _stagingDir,
                ["ServerConfiguration:DatabaseFileName"] = Path.GetFileName(_dbPath),
                ["ServerConfiguration:StoreId"] = "STORE-E2E-001",
                ["ServerConfiguration:StoreName"] = "Grozet Pilot E2E Store",
                ["ServerConfiguration:ServerId"] = "SRV-E2E-01",
                ["Serilog:MinimumLevel:Default"] = "Warning",
                ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
                ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Warning",
                ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Warning",
                ["Serilog:MinimumLevel:Override:RetailPOS"] = "Warning"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove existing DbContextOptions
            services.RemoveAll<DbContextOptions<RetailDbContext>>();

            // Re-register DbContext with isolated SQLite file connection in WAL mode
            var connString = $"Data Source={_dbPath};Cache=Shared";
            services.AddDbContext<RetailDbContext>((sp, options) =>
            {
                options.UseSqlite(connString);
                var gate = sp.GetService<IDatabaseOperationGate>();
                var scope = sp.GetService<IMaintenanceExecutionScope>();
                if (gate != null && scope != null)
                {
                    options.AddInterceptors(new DatabaseMaintenanceInterceptor(gate, scope));
                }
            });

            // Replace mock hardware with controlled MockHardwareDeviceManager instances
            services.RemoveAll<IReceiptPrinter>();
            services.AddSingleton<IReceiptPrinter>(HardwareManager.Printer);

            services.RemoveAll<ICashDrawer>();
            services.AddSingleton<ICashDrawer>(HardwareManager.Drawer);

            services.RemoveAll<IWeighingScale>();
            services.AddSingleton<IWeighingScale>(HardwareManager.Scale);

            // Configure isolated backup settings store pointing to fixture backups directory
            var backupSettingsPath = Path.Combine(_dataDir, "backup_settings.json");
            var settingsStore = new JsonBackupSettingsStore(backupSettingsPath);
            settingsStore.SaveSettingsAsync(new BackupConfiguration
            {
                BackupStoragePath = _backupsDir
            }).GetAwaiter().GetResult();
            services.RemoveAll<IBackupSettingsStore>();
            services.AddSingleton<IBackupSettingsStore>(settingsStore);

            // Register test middleware to inspect X-Test-Remote-IP header for simulated LAN remote IPs
            services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter());
        });
    }

    /// <summary>
    /// Creates a virtual POS counter client linked to this in-process server host,
    /// complete with its own terminal identity, headers, fault injection handler, and credentials.
    /// </summary>
    public VirtualPosTerminal CreateVirtualTerminal(
        string terminalCode,
        string terminalName,
        IPAddress ipAddress,
        Guid? terminalId = null,
        string? pairingToken = null)
    {
        var tid = terminalId ?? Guid.NewGuid();
        var token = pairingToken ?? $"TOKEN_{Guid.NewGuid():N}";
        var faultHandler = new FaultInjectingHttpMessageHandler();

        // Create client using WebApplicationFactory pipeline with fault handler
        var client = CreateDefaultClient(faultHandler);

        return new VirtualPosTerminal(
            tid,
            terminalCode,
            terminalName,
            ipAddress,
            token,
            client,
            faultHandler,
            HardwareManager);
    }

    /// <summary>
    /// Registers a terminal directly in the server's SQLite database with Active status and hashed pairing token.
    /// </summary>
    public async Task RegisterTerminalInDatabaseAsync(
        VirtualPosTerminal terminal,
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();

        var existing = await db.Terminals.FirstOrDefaultAsync(t => t.Id == terminal.TerminalId, cancellationToken);
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terminal.PairingToken)));

        if (existing == null)
        {
            var entity = new Terminal(
                terminal.TerminalId,
                terminal.TerminalCode,
                terminal.TerminalName,
                receiptPrinterName: "SimulatedPrinter",
                status: TerminalStatus.Active,
                pairingTokenHash: tokenHash,
                machineName: Environment.MachineName,
                clientIpAddress: terminal.IpAddress.ToString());

            db.Terminals.Add(entity);
        }
        else
        {
            // Activate existing
            existing.Activate();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Convenient helper to provision store profile and initial owner account.
    /// </summary>
    public async Task<string> SetupOwnerAsync(
        string storeName = "Grozet Pilot E2E Store",
        string username = "owner",
        string password = "OwnerPassword123!",
        string pin = "1234")
    {
        using var client = CreateClient();
        var req = new SetupOwnerRequest(storeName, username, "Store Owner", password, pin);
        var res = await client.PostAsJsonAsync("/api/v1/auth/setup-owner", req);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync();
            throw new HttpRequestException($"SetupOwner failed with {res.StatusCode}: {err}");
        }

        var loginRes = await client.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest(username, password));
        if (!loginRes.IsSuccessStatusCode)
        {
            var err = await loginRes.Content.ReadAsStringAsync();
            throw new HttpRequestException($"LoginPassword failed with {loginRes.StatusCode}: {err}");
        }

        var body = await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        return body!.Data!.Token;
    }

    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-Remote-IP", out var ipStr) &&
                        IPAddress.TryParse(ipStr.ToString(), out var ip))
                    {
                        context.Connection.RemoteIpAddress = ip;
                    }
                    else
                    {
                        context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    }
                    await nextMiddleware(context);
                });
                next(app);
            };
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !_disposed)
        {
            _disposed = true;
            CleanupFilesystem();
        }
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(ProtocolVersionConstants.ClientProtocolVersionHeader, ProtocolVersionConstants.CurrentProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        client.DefaultRequestHeaders.Add(ProtocolVersionConstants.ClientVersionHeader, "1.0.0");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (!_disposed)
        {
            _disposed = true;
            CleanupFilesystem();
        }
        GC.SuppressFinalize(this);
    }

    private void CleanupFilesystem()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // Best effort pool clear
        }

        if (PreserveDataDirectoryOnDispose)
        {
            return;
        }

        if (Directory.Exists(_dataDir))
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(_dataDir, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    Thread.Sleep(50 * attempt);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
