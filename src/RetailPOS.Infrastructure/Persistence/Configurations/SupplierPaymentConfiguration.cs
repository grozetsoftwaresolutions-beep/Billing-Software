using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.ToTable("supplier_payments");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.StoreId).IsRequired();
        builder.Property(p => p.SupplierId).IsRequired();
        builder.HasIndex(p => p.SupplierId).HasDatabaseName("IX_supplier_payments_supplier_id");

        builder.Property(p => p.Type).IsRequired();
        builder.Property(p => p.Amount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.Method).IsRequired();
        builder.Property(p => p.ReferenceNumber).HasMaxLength(50);
        builder.Property(p => p.PaymentDateUtc).IsRequired();
        builder.HasIndex(p => p.PaymentDateUtc).HasDatabaseName("IX_supplier_payments_payment_date");

        builder.Property(p => p.ClientTransactionId).IsRequired();
        builder.HasIndex(p => p.ClientTransactionId).IsUnique().HasDatabaseName("UK_supplier_payments_client_tx");

        builder.Property(p => p.CreatedByUserId).IsRequired();
        builder.Property(p => p.Notes).HasMaxLength(500);
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsRequired();

        builder.HasMany(p => p.Allocations)
            .WithOne()
            .HasForeignKey(a => a.SupplierPaymentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(SupplierPayment.Allocations))?.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class SupplierPaymentAllocationConfiguration : IEntityTypeConfiguration<SupplierPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentAllocation> builder)
    {
        builder.ToTable("supplier_payment_allocations");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        builder.Property(a => a.SupplierPaymentId).IsRequired();
        builder.HasIndex(a => a.SupplierPaymentId).HasDatabaseName("IX_supplier_payment_allocations_payment_id");

        builder.Property(a => a.PurchaseInvoiceId).IsRequired();
        builder.HasIndex(a => a.PurchaseInvoiceId).HasDatabaseName("IX_supplier_payment_allocations_invoice_id");

        builder.Property(a => a.AmountAllocated).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(a => a.CreatedAtUtc).IsRequired();
        builder.Property(a => a.UpdatedAtUtc).IsRequired();
    }
}
