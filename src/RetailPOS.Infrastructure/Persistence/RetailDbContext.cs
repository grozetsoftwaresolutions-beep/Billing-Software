using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;

namespace RetailPOS.Infrastructure.Persistence;

public class RetailDbContext : DbContext
{
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TaxCategory> TaxCategories => Set<TaxCategory>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<SaleInvoice> SalesInvoices => Set<SaleInvoice>();
    public DbSet<SaleItem> SalesItems => Set<SaleItem>();
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<InvoiceSequence> InvoiceSequences => Set<InvoiceSequence>();

    public RetailDbContext(DbContextOptions<RetailDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RetailDbContext).Assembly);
    }
}
