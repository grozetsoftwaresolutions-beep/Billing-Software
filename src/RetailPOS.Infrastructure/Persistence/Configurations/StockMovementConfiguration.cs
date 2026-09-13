using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();

        builder.Property(m => m.ProductId).IsRequired();
        builder.HasIndex(m => m.ProductId).HasDatabaseName("IX_stock_movements_product_id");

        builder.Property(m => m.MovementType).IsRequired();
        builder.HasIndex(m => m.MovementType).HasDatabaseName("IX_stock_movements_type");

        builder.Property(m => m.QuantityChanged)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("quantity_changed")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(m => m.ResultingQuantity)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("resulting_quantity")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(m => m.ReferenceNumber).HasMaxLength(100);
        builder.Property(m => m.Reason).HasMaxLength(250);
        builder.Property(m => m.UserId);
        builder.Property(m => m.TerminalId);

        builder.HasIndex(m => m.CreatedAtUtc).HasDatabaseName("IX_stock_movements_created_at");
    }
}
