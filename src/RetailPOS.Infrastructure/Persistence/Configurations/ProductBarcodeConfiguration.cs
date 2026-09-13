using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class ProductBarcodeConfiguration : IEntityTypeConfiguration<ProductBarcode>
{
    public void Configure(EntityTypeBuilder<ProductBarcode> builder)
    {
        builder.ToTable("product_barcodes");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Barcode)
            .HasConversion<BarcodeToStringConverter>()
            .HasColumnName("barcode")
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(b => b.Barcode)
            .IsUnique()
            .HasDatabaseName("UK_product_barcodes_barcode");

        builder.Property(b => b.ProductId).IsRequired();
        builder.HasIndex(b => b.ProductId).HasDatabaseName("IX_product_barcodes_product_id");

        builder.Property(b => b.IsPrimary).IsRequired().HasDefaultValue(false);
        builder.Property(b => b.IsActive).IsRequired().HasDefaultValue(true);
    }
}
