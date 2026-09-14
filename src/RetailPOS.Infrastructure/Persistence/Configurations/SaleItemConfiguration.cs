using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> builder)
    {
        builder.ToTable("sales_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedOnAdd();

        builder.Property(i => i.SaleId).IsRequired();
        builder.HasIndex(i => i.SaleId).HasDatabaseName("IX_sales_items_sale_id");

        builder.Property(i => i.ProductId).IsRequired();
        builder.HasIndex(i => i.ProductId).HasDatabaseName("IX_sales_items_product_id");

        builder.Property(i => i.ProductSKU).HasMaxLength(50).IsRequired();
        builder.Property(i => i.ProductName).HasMaxLength(200).IsRequired();

        builder.Property(i => i.Quantity)
            .HasConversion<QuantityToStringConverter>()
            .HasColumnName("quantity")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(i => i.TaxRatePercentage).HasColumnType("TEXT").IsRequired();

        // Historical snapshots stored as INTEGER (paise)
        builder.Property(i => i.UnitSellingPrice).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.UnitCostPrice).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.GrossAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.DiscountAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.TaxableAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.TaxAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(i => i.LineTotal).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();

        builder.HasMany(i => i.TaxComponents)
            .WithOne()
            .HasForeignKey(tc => tc.SaleItemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(i => i.TaxComponents).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
