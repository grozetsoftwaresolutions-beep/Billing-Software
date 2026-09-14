using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Services;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Security;
using RetailPOS.Server.Authentication;
using RetailPOS.Server.Configuration;
using RetailPOS.Server.Endpoints;
using RetailPOS.Server.Middleware;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Enable running as a Windows Service with appropriate service lifetime
builder.Host.UseWindowsService();

// Strongly typed server configuration
var serverOptions = new ServerOptions();
builder.Configuration.GetSection(ServerOptions.SectionName).Bind(serverOptions);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

// Configure Serilog structured logging with rolling file and console
var logsDir = serverOptions.ResolveLogsDirectory();
var logFilePath = Path.Combine(logsDir, "retailpos-server-.log");

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}",
            formatProvider: CultureInfo.InvariantCulture)
        .WriteTo.File(
            logFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}",
            formatProvider: CultureInfo.InvariantCulture);
});

// Configure Kestrel bindings: Localhost for single-PC or 0.0.0.0 for wired LAN
builder.WebHost.ConfigureKestrel(options =>
{
    var listenAddress = serverOptions.BindToAllInterfaces ? IPAddress.Any : IPAddress.Loopback;
    options.Listen(listenAddress, serverOptions.HttpPort);

    if (serverOptions.UseHttps)
    {
        options.Listen(listenAddress, serverOptions.HttpsPort, listenOptions =>
        {
            listenOptions.UseHttps();
        });
    }
});

// Authoritative local SQLite database registration
builder.Services.AddDbContext<RetailDbContext>(options =>
{
    options.UseSqlite(serverOptions.BuildSqliteConnectionString());
});

// Register persistence repositories and unit of work
builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
builder.Services.AddScoped<IUserRepository, EfUserRepository>();
builder.Services.AddScoped<ITerminalRepository, EfTerminalRepository>();
builder.Services.AddScoped<IAuditLogRepository, EfAuditLogRepository>();
builder.Services.AddScoped<IStoreRepository, EfStoreRepository>();
builder.Services.AddScoped<IProductRepository, EfProductRepository>();
builder.Services.AddScoped<IInventoryRepository, EfInventoryRepository>();
builder.Services.AddScoped<ICategoryRepository, EfCategoryRepository>();
builder.Services.AddScoped<ISaleRepository, EfSaleRepository>();
builder.Services.AddScoped<ISaleRefundRepository, EfSaleRefundRepository>();
builder.Services.AddScoped<IInvoiceSequenceRepository, EfInvoiceSequenceRepository>();

// Register cryptographic security and session management
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<LocalSessionManager>();
builder.Services.AddSingleton<ILocalSessionValidator>(sp => sp.GetRequiredService<LocalSessionManager>());
builder.Services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<LocalSessionManager>());

// Register application domain services
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ITerminalService, TerminalService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IStoreService, StoreService>();
builder.Services.AddScoped<ISalesService, SalesService>();
builder.Services.AddScoped<IPricingCalculator, PricingCalculator>();

// Register infrastructure services
builder.Services.AddScoped<IDatabaseBackupService, SqliteDatabaseBackupService>();
builder.Services.AddScoped<AtomicStockManager>();
builder.Services.AddScoped<InvoiceSequenceGenerator>();

var app = builder.Build();

// Pipeline middleware order: Correlation ID -> Global Exceptions -> Terminal Validation
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<TerminalValidationMiddleware>();

// Health and monitoring endpoints
app.MapHealthEndpoints();

// Phase 4 Authentication, User and Terminal management endpoints
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapTerminalEndpoints();

// Phase 5 Product, Inventory and Catalog endpoints
app.MapProductEndpoints();
app.MapInventoryEndpoints();
app.MapCategoryEndpoints();

// Phase 6 Sales, Billing and Store configuration endpoints
app.MapSaleEndpoints();
app.MapStoreEndpoints();

// Safe initialization of database and default seed configuration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
    await DatabaseInitializer.InitializeDatabaseAsync(db);
}

app.Run();

public partial class Program { }
