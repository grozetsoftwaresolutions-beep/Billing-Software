using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Taxes;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class TaxCategoryConfiguration : IEntityTypeConfiguration<TaxCategory>
{
    public void Configure(EntityTypeBuilder<TaxCategory> builder)
    {
        builder.ToTable("tax_categories");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Code).HasMaxLength(50).IsRequired();
        builder.HasIndex(t => t.Code).IsUnique().HasDatabaseName("UK_tax_categories_code");

        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.IsExempt).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.IsActive).IsRequired().HasDefaultValue(true);

        builder.OwnsMany(t => t.Components, cb =>
        {
            cb.ToTable("tax_components");
            cb.WithOwner().HasForeignKey("tax_category_id");
            cb.Property<int>("id");
            cb.HasKey("id");
            cb.Property(c => c.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
            cb.Property(c => c.RatePercentage).HasColumnName("rate_percentage").HasColumnType("TEXT").IsRequired();
        });
    }
}
