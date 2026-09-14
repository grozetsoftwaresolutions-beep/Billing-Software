using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class SaleInvoiceConfiguration : IEntityTypeConfiguration<SaleInvoice>
{
    public void Configure(EntityTypeBuilder<SaleInvoice> builder)
    {
        builder.ToTable("sales_invoices");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.InvoiceNumber).HasMaxLength(50).IsRequired();
        builder.HasIndex(s => s.InvoiceNumber).IsUnique().HasDatabaseName("UK_sales_invoices_invoice_number");

        // Idempotency: Unique ClientTransactionId ensures no duplicate sales can be committed
        builder.Property(s => s.ClientTransactionId).IsRequired();
        builder.HasIndex(s => s.ClientTransactionId).IsUnique().HasDatabaseName("UK_sales_invoices_client_tx_id");

        builder.Property(s => s.TerminalId).IsRequired();
        builder.HasIndex(s => s.TerminalId).HasDatabaseName("IX_sales_invoices_terminal_id");

        builder.Property(s => s.CashierId).IsRequired();
        builder.HasIndex(s => s.CashierId).HasDatabaseName("IX_sales_invoices_cashier_id");

        builder.Property(s => s.CustomerId);
        builder.HasIndex(s => s.CustomerId).HasDatabaseName("IX_sales_invoices_customer_id");

        builder.Property(s => s.PricingModel).IsRequired();
        builder.Property(s => s.Status).IsRequired();
        builder.HasIndex(s => s.CreatedAtUtc).HasDatabaseName("IX_sales_invoices_created_at");

        // All financial amounts persisted as INTEGER (paise)
        builder.Property(s => s.GrossSubtotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalLineDiscounts).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.InvoiceDiscount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TaxableSubtotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalTax).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.RoundingAdjustment).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.FinalPayable).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();

        // DeleteBehavior.Restrict protects historical sales from accidental deletion
        builder.HasMany(s => s.Items)
            .WithOne()
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(s => s.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(s => s.Payments)
            .WithOne()
            .HasForeignKey(p => p.SaleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(s => s.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
