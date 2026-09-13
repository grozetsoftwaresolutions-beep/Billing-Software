using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class PaymentRecordConfiguration : IEntityTypeConfiguration<PaymentRecord>
{
    public void Configure(EntityTypeBuilder<PaymentRecord> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.SaleId).IsRequired();
        builder.HasIndex(p => p.SaleId).HasDatabaseName("IX_payments_sale_id");

        builder.Property(p => p.Method).IsRequired();
        builder.HasIndex(p => p.Method).HasDatabaseName("IX_payments_method");

        builder.Property(p => p.Amount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.TenderedAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.ChangeAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();

        builder.Property(p => p.ReferenceNumber).HasMaxLength(100);
    }
}
