using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(150).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(50);
        builder.HasIndex(c => c.Phone).HasDatabaseName("IX_customers_phone");

        builder.Property(c => c.Email).HasMaxLength(100);
        builder.Property(c => c.OutstandingCredit).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(c => c.CreditLimit).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(c => c.IsActive).IsRequired().HasDefaultValue(true);
    }
}
