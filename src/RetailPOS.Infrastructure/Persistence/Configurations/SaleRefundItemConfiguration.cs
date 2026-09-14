using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class SaleRefundItemConfiguration : IEntityTypeConfiguration<SaleRefundItem>
{
    public void Configure(EntityTypeBuilder<SaleRefundItem> builder)
    {
        builder.ToTable("sale_refund_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedOnAdd();

        builder.Property(i => i.RefundId).IsRequired();
        builder.HasIndex(i => i.RefundId).HasDatabaseName("IX_sale_refund_items_refund_id");

        builder.Property(i => i.SaleItemId).IsRequired();
        builder.HasIndex(i => i.SaleItemId).HasDatabaseName("IX_sale_refund_items_sale_item_id");

        builder.Property(i => i.ProductId).IsRequired();

        builder.Property(i => i.QuantityRefunded)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("quantity_refunded")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(i => i.RefundAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.TaxRefundAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
    }
}
