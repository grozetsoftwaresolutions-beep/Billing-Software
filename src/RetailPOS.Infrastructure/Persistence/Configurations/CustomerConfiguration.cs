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
        builder.HasIndex(c => c.Name).HasDatabaseName("IX_customers_name");

        builder.Property(c => c.Phone).HasMaxLength(20);
        builder.HasIndex(c => c.Phone).IsUnique().HasFilter("Phone IS NOT NULL").HasDatabaseName("UK_customers_phone");

        builder.Property(c => c.Email).HasMaxLength(100);
        builder.Property(c => c.Address).HasMaxLength(300);
        builder.Property(c => c.Gstin).HasMaxLength(15);
        builder.HasIndex(c => c.Gstin).HasDatabaseName("IX_customers_gstin");

        builder.Property(c => c.OutstandingCredit).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(c => c.CreditBalance).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(c => c.CreditLimit).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(c => c.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(c => c.Notes).HasMaxLength(500);
        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc).IsRequired();
    }
}

public sealed class CustomerPaymentConfiguration : IEntityTypeConfiguration<CustomerPayment>
{
    public void Configure(EntityTypeBuilder<CustomerPayment> builder)
    {
        builder.ToTable("customer_payments");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.StoreId).IsRequired();
        builder.Property(p => p.CustomerId).IsRequired();
        builder.HasIndex(p => p.CustomerId).HasDatabaseName("IX_customer_payments_customer_id");

        builder.Property(p => p.Amount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.Method).IsRequired();
        builder.Property(p => p.ReferenceNumber).HasMaxLength(50);
        builder.Property(p => p.PaymentDateUtc).IsRequired();

        builder.Property(p => p.ClientTransactionId).IsRequired();
        builder.HasIndex(p => p.ClientTransactionId).IsUnique().HasDatabaseName("UK_customer_payments_client_tx");

        builder.Property(p => p.CreatedByUserId).IsRequired();
        builder.Property(p => p.Notes).HasMaxLength(500);
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsRequired();

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(p => p.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
