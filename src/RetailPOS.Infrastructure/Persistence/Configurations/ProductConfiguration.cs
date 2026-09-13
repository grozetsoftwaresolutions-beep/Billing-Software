using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.SKU).HasMaxLength(50).IsRequired();
        builder.HasIndex(p => p.SKU).IsUnique().HasDatabaseName("UK_products_sku");

        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(p => p.Name).HasDatabaseName("IX_products_name");

        builder.Property(p => p.CostPrice)
            .HasConversion<MoneyToMinorUnitsConverter>()
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(p => p.SellingPrice)
            .HasConversion<MoneyToMinorUnitsConverter>()
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(p => p.Unit)
            .HasConversion<UnitOfMeasureToStringConverter>()
            .HasColumnName("unit_code")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.TaxCategoryId).IsRequired();
        builder.HasIndex(p => p.TaxCategoryId).HasDatabaseName("IX_products_tax_category_id");

        builder.Property(p => p.CategoryId);
        builder.HasIndex(p => p.CategoryId).HasDatabaseName("IX_products_category_id");

        builder.Property(p => p.BrandId);
        builder.HasIndex(p => p.BrandId).HasDatabaseName("IX_products_brand_id");

        builder.Property(p => p.IsActive).IsRequired().HasDefaultValue(true);
        builder.HasIndex(p => p.IsActive).HasDatabaseName("IX_products_is_active");

        builder.Property(p => p.Notes).HasMaxLength(500);

        builder.HasMany(p => p.Barcodes)
            .WithOne()
            .HasForeignKey(b => b.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
