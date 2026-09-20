using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Server.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    public string DatabasePath => _dbPath;

    public CustomWebApplicationFactory()
    {
        var id = Guid.NewGuid().ToString("N");
        _dataDir = Path.Combine(Path.GetTempPath(), $"retailpos_test_dir_{id}");
        Directory.CreateDirectory(_dataDir);
        _dbPath = Path.Combine(_dataDir, $"retailpos_test_{id}.db");
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "RetailPOS.sln")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var repoRoot = FindRepoRoot();
        var serverContentRoot = Path.Combine(repoRoot, "src", "RetailPOS.Server");
        if (Directory.Exists(serverContentRoot))
        {
            builder.UseContentRoot(serverContentRoot);
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ServerConfiguration:DataDirectory"] = _dataDir,
                ["ServerConfiguration:LogsDirectory"] = _dataDir,
                ["ServerConfiguration:BackupsDirectory"] = _dataDir,
                ["ServerConfiguration:SecurityDirectory"] = Path.Combine(_dataDir, "Security"),
                ["ServerConfiguration:StagingDirectory"] = Path.Combine(_dataDir, "Staging"),
                ["ServerConfiguration:DatabaseFileName"] = Path.GetFileName(_dbPath),
                ["ServerConfiguration:StoreId"] = "STORE-TEST-001",
                ["ServerConfiguration:StoreName"] = "Grozet Test Store",
                ["ServerConfiguration:ServerId"] = "SRV-TEST-01"
            });
        });

        builder.ConfigureServices(services =>
        {
            var dbContextDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<RetailDbContext>));
            if (dbContextDescriptor != null)
            {
                services.Remove(dbContextDescriptor);
            }

            var connString = $"Data Source={_dbPath};Cache=Shared";
            services.AddDbContext<RetailDbContext>(options =>
            {
                options.UseSqlite(connString);
            });

            // Add test middleware to simulate remote IP if X-Test-Remote-IP header is present
            services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter());
        });
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

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(RetailPOS.Contracts.Common.ProtocolVersionConstants.ClientProtocolVersionHeader, RetailPOS.Contracts.Common.ProtocolVersionConstants.CurrentProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        client.DefaultRequestHeaders.Add(RetailPOS.Contracts.Common.ProtocolVersionConstants.ClientVersionHeader, "1.0.0");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDir))
        {
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch
            {
                // Best effort in temp directory
            }
        }
    }
}
