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

    public CustomWebApplicationFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpos_test_{Guid.NewGuid():N}.db");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ServerConfiguration:DataDirectory"] = Path.GetTempPath(),
                ["ServerConfiguration:LogsDirectory"] = Path.GetTempPath(),
                ["ServerConfiguration:BackupsDirectory"] = Path.GetTempPath(),
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
                    await nextMiddleware(context);
                });
                next(app);
            };
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch
            {
                // Best effort in temp directory
            }
        }
    }
}
