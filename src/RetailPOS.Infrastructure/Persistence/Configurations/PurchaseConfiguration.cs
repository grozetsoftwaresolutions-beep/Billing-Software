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

        builder.Property(p => p.InvoiceNumber).HasMaxLength(100).IsRequired();
        builder.HasIndex(p => p.InvoiceNumber).IsUnique().HasDatabaseName("UK_purchase_invoices_number");

        builder.Property(p => p.SupplierId).IsRequired();
        builder.HasIndex(p => p.SupplierId).HasDatabaseName("IX_purchase_invoices_supplier_id");

        builder.Property(p => p.Subtotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.TotalTax).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.GrandTotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.Status).IsRequired();

        builder.HasMany(p => p.Items)
            .WithOne()
            .HasForeignKey(i => i.PurchaseId)
            .OnDelete(DeleteBehavior.Restrict);
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

        builder.Property(i => i.Quantity)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("quantity")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(i => i.UnitCost).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.TaxRatePercentage).HasColumnType("TEXT").IsRequired();
        builder.Property(i => i.TaxAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.LineTotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
    }
}
