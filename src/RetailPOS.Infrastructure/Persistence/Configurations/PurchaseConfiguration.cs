using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class PurchaseInvoiceConfiguration : IEntityTypeConfiguration<PurchaseInvoice>
{
    public void Configure(EntityTypeBuilder<PurchaseInvoice> builder)
    {
        builder.ToTable("purchase_invoices");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.StoreId).IsRequired();
        builder.Property(p => p.InvoiceNumber).HasMaxLength(100).IsRequired();
        builder.HasIndex(p => p.InvoiceNumber).IsUnique().HasDatabaseName("UK_purchase_invoices_number");

        builder.Property(p => p.SupplierInvoiceNumber).HasMaxLength(100).IsRequired();
        builder.Property(p => p.SupplierId).IsRequired();
        builder.HasIndex(p => p.SupplierId).HasDatabaseName("IX_purchase_invoices_supplier_id");

        builder.Property(p => p.InvoiceDate)
            .HasConversion(
                d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.ReceivedDateUtc).IsRequired();
        builder.HasIndex(p => p.ReceivedDateUtc).HasDatabaseName("IX_purchase_invoices_received_at");

        builder.Property(p => p.Subtotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.TotalTax).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.GrandTotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.CreditOffsetAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.AmountPaid).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.BalanceDue).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();

        builder.Property(p => p.PaymentStatus).IsRequired();
        builder.Property(p => p.Status).IsRequired();

        builder.Property(p => p.ClientTransactionId);
        builder.HasIndex(p => p.ClientTransactionId).HasDatabaseName("UK_purchase_invoices_client_tx");

        builder.Property(p => p.CancellationClientTransactionId);
        builder.HasIndex(p => p.CancellationClientTransactionId).HasDatabaseName("UK_purchase_invoices_cancel_tx");

        builder.Property(p => p.CancellationReason).HasMaxLength(250);
        builder.Property(p => p.Notes).HasMaxLength(500);
        builder.Property(p => p.CreatedByUserId).IsRequired();
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsRequired();

        builder.HasMany(p => p.Items)
            .WithOne()
            .HasForeignKey(i => i.PurchaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Metadata.FindNavigation(nameof(PurchaseInvoice.Items))?.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class PurchaseItemConfiguration : IEntityTypeConfiguration<PurchaseItem>
{
    public void Configure(EntityTypeBuilder<PurchaseItem> builder)
    {
        builder.ToTable("purchase_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedOnAdd();

        builder.Property(i => i.PurchaseId).IsRequired();
        builder.HasIndex(i => i.PurchaseId).HasDatabaseName("IX_purchase_items_purchase_id");

        builder.Property(i => i.ProductId).IsRequired();
        builder.HasIndex(i => i.ProductId).HasDatabaseName("IX_purchase_items_product_id");

        builder.Property(i => i.ProductSku).HasMaxLength(50).IsRequired();
        builder.Property(i => i.ProductName).HasMaxLength(150).IsRequired();
        builder.Property(i => i.UnitOfMeasure).HasMaxLength(20).IsRequired();

        builder.Property(i => i.Quantity)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("quantity")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(i => i.UnitCost).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.TaxRatePercentage).HasColumnType("TEXT").IsRequired();
        builder.Property(i => i.TaxAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.LineTotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.BatchNumber).HasMaxLength(50);

        builder.Property(i => i.ExpiryDate)
            .HasConversion(
                d => d.HasValue ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                s => !string.IsNullOrEmpty(s) ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null)
            .HasColumnType("TEXT");

        builder.HasMany(i => i.TaxComponents)
            .WithOne()
            .HasForeignKey(tc => tc.PurchaseItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(PurchaseItem.TaxComponents))?.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class PurchaseItemTaxComponentConfiguration : IEntityTypeConfiguration<PurchaseItemTaxComponent>
{
    public void Configure(EntityTypeBuilder<PurchaseItemTaxComponent> builder)
    {
        builder.ToTable("purchase_item_tax_components");
        builder.HasKey(tc => tc.Id);
        builder.Property(tc => tc.Id).ValueGeneratedOnAdd();

        builder.Property(tc => tc.PurchaseItemId).IsRequired();
        builder.HasIndex(tc => tc.PurchaseItemId).HasDatabaseName("IX_purchase_item_tax_components_item_id");

        builder.Property(tc => tc.ComponentName).HasMaxLength(50).IsRequired();
        builder.Property(tc => tc.RatePercentage).HasColumnType("TEXT").IsRequired();
        builder.Property(tc => tc.TaxAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();

        builder.HasIndex(tc => new { tc.PurchaseItemId, tc.ComponentName })
            .IsUnique()
            .HasDatabaseName("UK_purchase_item_tax_components_item_component");
    }
}
