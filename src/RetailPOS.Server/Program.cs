using RetailPOS.Infrastructure.Networking;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence.Interceptors;
using RetailPOS.Server.BackgroundServices;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailPOS.Application.Hardware;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Domain.Services;
using RetailPOS.Infrastructure.Hardware;
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

// Authoritative local SQLite database registration with Phase 11 Database Maintenance Interceptor
builder.Services.AddDbContext<RetailDbContext>((sp, options) =>
{
    options.UseSqlite(serverOptions.BuildSqliteConnectionString());
    var gate = sp.GetService<IDatabaseOperationGate>();
    var scope = sp.GetService<IMaintenanceExecutionScope>();
    if (gate != null && scope != null)
    {
        options.AddInterceptors(new DatabaseMaintenanceInterceptor(gate, scope));
    }
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
builder.Services.AddScoped<ISupplierRepository, EfSupplierRepository>();
builder.Services.AddScoped<IPurchaseRepository, EfPurchaseRepository>();
builder.Services.AddScoped<ICustomerRepository, EfCustomerRepository>();
builder.Services.AddScoped<ICashSessionRepository, EfCashSessionRepository>();
builder.Services.AddScoped<ITerminalHardwareConfigRepository, TerminalHardwareConfigRepository>();

// Phase 11 Backup, Restore & Disaster Recovery services
builder.Services.AddSingleton<IDatabaseOperationGate, DatabaseOperationGate>();
builder.Services.AddSingleton<IDatabaseMaintenanceCoordinator, DatabaseMaintenanceCoordinator>();
builder.Services.AddSingleton<IBackupOperationCoordinator, BackupOperationCoordinator>();
builder.Services.AddSingleton<MaintenanceScopeFactory>();
builder.Services.AddSingleton<IMaintenanceScopeFactory>(sp => sp.GetRequiredService<MaintenanceScopeFactory>());
builder.Services.AddSingleton<IMaintenanceExecutionScope>(sp => sp.GetRequiredService<MaintenanceScopeFactory>());
builder.Services.AddSingleton<IStoreIdentityService, StoreIdentityService>();
builder.Services.AddSingleton<IBackupManifestService, BackupManifestService>();
builder.Services.AddSingleton<IBackupSettingsStore, JsonBackupSettingsStore>();
builder.Services.AddScoped<IBackupRepository, EfBackupRepository>();
builder.Services.AddScoped<IBackupRetentionService, BackupRetentionService>();
builder.Services.AddScoped<IFilesystemReconciliationEngine, FilesystemReconciliationEngine>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddHostedService<ScheduledBackupBackgroundService>();

// Register cryptographic security and session management
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<LocalSessionManager>(sp =>
{
    var hasher = sp.GetRequiredService<IPasswordHasher>();
    return new LocalSessionManager(hasher, () => sp.CreateScope().ServiceProvider.GetRequiredService<RetailDbContext>());
});
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
builder.Services.AddScoped<ISupplierService, SupplierService>();
builder.Services.AddScoped<IPurchaseService, PurchaseService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<ICashSessionService, CashSessionService>();
builder.Services.AddScoped<IReportRepository, EfReportRepository>();
builder.Services.AddScoped<IReportService, ReportService>();


// Phase 13 Security Settings, Supervisor Authorization & Immutable Audit
builder.Services.AddSingleton<ISecuritySettingsStore>(sp =>
{
    var options = sp.GetService<Microsoft.Extensions.Options.IOptions<RetailPOS.Server.Configuration.ServerOptions>>()?.Value;
    var configDir = options != null && !string.IsNullOrWhiteSpace(options.DataDirectory)
        ? Path.Combine(options.DataDirectory, "Config")
        : null;
    var auditRepo = sp.GetService<IAuditLogRepository>();
    return new SecuritySettingsStore(
        customConfigDir: configDir,
        auditRepo: auditRepo,
        dbContextFactory: () => sp.CreateScope().ServiceProvider.GetRequiredService<RetailDbContext>());
});
builder.Services.AddScoped<ISupervisorAuthorizationCore, SupervisorAuthorizationCore>();
builder.Services.AddSingleton<ISupervisorApprovalService, SupervisorApprovalService>();
builder.Services.AddScoped<IAuditService, AuditService>();

// Phase 12 Multi-Counter Networking, Terminal Registration & Concurrency Hardening
builder.Services.AddSingleton<ITerminalHeartbeatTracker, TerminalHeartbeatTracker>();
builder.Services.AddSingleton<INetworkDiagnosticsService, NetworkDiagnosticsService>();
builder.Services.AddSingleton<NonceReplayCache>();
builder.Services.AddScoped<IServerIdentityService>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RetailPOS.Server.Configuration.ServerOptions>>().Value;
    var securityDir = Path.Combine(options.DataDirectory, "Security");
    return new ServerIdentityService(
        sp.GetService<Microsoft.EntityFrameworkCore.IDbContextFactory<RetailDbContext>>(),
        sp.GetService<RetailDbContext>(),
        sp.GetService<IStoreIdentityService>(),
        sp.GetService<IAuditLogRepository>(),
        customSecurityDir: securityDir,
        nonceCache: sp.GetRequiredService<NonceReplayCache>());
});
builder.Services.AddScoped<ITerminalEnrollmentTokenRepository, EfTerminalEnrollmentTokenRepository>();
builder.Services.AddScoped<ITerminalEnrollmentService, TerminalEnrollmentService>();
builder.Services.AddHostedService<UdpServerDiscoveryListener>();

// Phase 10 Hardware Services and Hardware Adapters (Simulated for default server environment)
builder.Services.AddScoped<ITerminalHardwareConfigService, TerminalHardwareConfigService>();
builder.Services.AddScoped<IReceiptService, ReceiptService>();
builder.Services.AddScoped<ICashDrawerService, CashDrawerService>();
builder.Services.AddScoped<IBarcodeScannerService, BarcodeScannerService>();
builder.Services.AddScoped<IWeighingScaleService, WeighingScaleService>();

builder.Services.AddSingleton<IReceiptPrinter, SimulatedReceiptPrinter>();
builder.Services.AddSingleton<ICashDrawer, SimulatedCashDrawer>();
builder.Services.AddSingleton<IWeighingScale, SimulatedWeighingScale>();
builder.Services.AddSingleton<IScaleProtocolParser, GenericAsciiScaleParser>();

// Register infrastructure services
builder.Services.AddScoped<IDatabaseBackupService, SqliteDatabaseBackupService>();
builder.Services.AddScoped<AtomicStockManager>();
builder.Services.AddScoped<InvoiceSequenceGenerator>();

var app = builder.Build();

// Pipeline middleware order: Correlation ID -> Global Exceptions -> Terminal Validation
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<TransportSecurityMiddleware>();
app.UseMiddleware<LoopbackRecoveryMiddleware>();
app.UseMiddleware<TerminalValidationMiddleware>();
app.UseMiddleware<DatabaseMaintenanceMiddleware>();
app.UseMiddleware<SessionInactivityMiddleware>();

// Health and monitoring endpoints
app.MapHealthEndpoints();
app.MapDiscoveryEndpoints();

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

// Phase 7 Purchases and Supplier endpoints
app.MapSupplierEndpoints();
app.MapPurchaseEndpoints();

// Phase 8 Customer and Cash Session endpoints
app.MapCustomerEndpoints();
app.MapCashSessionEndpoints();

// Phase 9 Reports & Analytics endpoints
app.MapReportEndpoints();

// Phase 10 Hardware and Terminal Configuration endpoints
app.MapTerminalHardwareConfigEndpoints();
app.MapHardwareEndpoints();


// Phase 13 Supervisor Approval, Audit and Security Settings endpoints
app.MapSupervisorEndpoints();
app.MapAuditEndpoints();
app.MapSecuritySettingsEndpoints();

// Phase 11 Backup, Restore and Recovery endpoints
app.MapBackupEndpoints();
app.MapAdminRecoveryEndpoints();

// Safe initialization of database and default seed configuration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
    await DatabaseInitializer.InitializeDatabaseAsync(db);

    // Phase 11 Filesystem Crash Reconciliation & Store Identity initialization
    var reconciler = scope.ServiceProvider.GetRequiredService<IFilesystemReconciliationEngine>();
    await reconciler.ReconcileStartupAsync();

    var storeRepo = scope.ServiceProvider.GetRequiredService<IStoreRepository>();
    var store = await storeRepo.GetStoreAsync();
    var storeIdentity = scope.ServiceProvider.GetRequiredService<IStoreIdentityService>();
    await storeIdentity.InitializeOrLoadAsync(store != null ? Guid.Parse("11111111-1111-1111-1111-111111111111") : Guid.NewGuid(), store?.StoreName ?? "Default Store");

    
    // Phase 13 Security Settings Store Initialization (Startup Reconciliation States A, B, C, D)
    var settingsStore = scope.ServiceProvider.GetRequiredService<ISecuritySettingsStore>();
    await settingsStore.InitializeAsync();

// Phase 12 Server Identity initialization
    var serverIdentity = scope.ServiceProvider.GetRequiredService<IServerIdentityService>();
    await serverIdentity.EvaluateStartupStateAsync();
}

if (args.Contains("--provision-replacement-identity"))
{
    using var cliScope = app.Services.CreateScope();
    var serverIdentityCli = cliScope.ServiceProvider.GetRequiredService<IServerIdentityService>();
    Console.Write("Enter StoreMasterRecoveryKey: ");
    var key = Console.ReadLine();
    if (!string.IsNullOrWhiteSpace(key))
    {
        var req = new RetailPOS.Contracts.Terminals.ProvisionReplacementIdentityRequestDto(key.Trim());
        var result = await serverIdentityCli.ProvisionReplacementIdentityAsync(req);
        Console.WriteLine($"Replacement Identity Provisioned: {result.HumanFingerprint}");
    }
    return;
}

app.Run();

public partial class Program { }
