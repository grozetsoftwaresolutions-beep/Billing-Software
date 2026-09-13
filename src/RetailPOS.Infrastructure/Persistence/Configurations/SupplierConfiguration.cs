using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("suppliers");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(150).IsRequired();
        builder.HasIndex(s => s.Name).HasDatabaseName("IX_suppliers_name");

        builder.Property(s => s.ContactPerson).HasMaxLength(100);
        builder.Property(s => s.Phone).HasMaxLength(50);
        builder.Property(s => s.TaxNumber).HasMaxLength(50);
        builder.Property(s => s.BalanceDue).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.IsActive).IsRequired().HasDefaultValue(true);
    }
}
