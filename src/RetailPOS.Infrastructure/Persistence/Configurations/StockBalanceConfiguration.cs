using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> builder)
    {
        builder.ToTable("stock_balances");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ProductId).IsRequired();
        builder.HasIndex(s => s.ProductId).IsUnique().HasDatabaseName("UK_stock_balances_product_id");

        builder.Property(s => s.CurrentQuantity)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("current_quantity")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(s => s.ReorderLevel)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("reorder_level")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(s => s.IsLowStock)
            .HasColumnName("is_low_stock")
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasIndex(s => s.IsLowStock).HasDatabaseName("IX_stock_balances_is_low_stock");
    }
}
