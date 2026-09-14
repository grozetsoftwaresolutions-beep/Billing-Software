using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class SaleItemTaxComponentConfiguration : IEntityTypeConfiguration<SaleItemTaxComponent>
{
    public void Configure(EntityTypeBuilder<SaleItemTaxComponent> builder)
    {
        builder.ToTable("sale_item_tax_components");
        builder.HasKey(tc => tc.Id);
        builder.Property(tc => tc.Id).ValueGeneratedOnAdd();

        builder.Property(tc => tc.SaleItemId).IsRequired();
        builder.HasIndex(tc => tc.SaleItemId).HasDatabaseName("IX_sale_item_tax_components_sale_item_id");
        builder.HasIndex(tc => new { tc.SaleItemId, tc.ComponentName }).IsUnique().HasDatabaseName("UK_sale_item_tax_components_item_component");

        builder.Property(tc => tc.ComponentName).HasMaxLength(50).IsRequired();
        builder.Property(tc => tc.RatePercentage).HasColumnType("TEXT").IsRequired();
        builder.Property(tc => tc.Amount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(tc => tc.CreatedAtUtc).IsRequired();
    }
}
