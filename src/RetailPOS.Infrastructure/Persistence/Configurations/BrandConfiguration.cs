using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Catalog;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.ToTable("brands");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Name).HasMaxLength(100).UseCollation("NOCASE").IsRequired();
        builder.HasIndex(b => b.Name).IsUnique().HasDatabaseName("UK_brands_name");
        builder.Property(b => b.IsActive).IsRequired().HasDefaultValue(true);
    }
}
