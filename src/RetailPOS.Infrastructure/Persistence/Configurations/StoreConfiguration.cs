using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Configuration;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.ToTable("stores");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.StoreName).HasMaxLength(150).IsRequired();
        builder.Property(s => s.Address).HasMaxLength(300);
        builder.Property(s => s.Phone).HasMaxLength(50);
        builder.Property(s => s.TaxRegistrationNumber).HasMaxLength(50);
        builder.Property(s => s.ReceiptFooterMessage).HasMaxLength(300);

        builder.Property(s => s.CashRoundingEnabled).HasColumnName("cash_rounding_enabled").HasDefaultValue(true);
        builder.Property(s => s.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(100).HasDefaultValue("India Standard Time");

        builder.OwnsOne(s => s.Currency, cb =>
        {
            cb.Property(c => c.Code).HasColumnName("currency_code").HasMaxLength(10).HasDefaultValue("INR");
            cb.Property(c => c.Symbol).HasColumnName("currency_symbol").HasMaxLength(10).HasDefaultValue("₹");
            cb.Property(c => c.MinorUnitsPerMajorUnit).HasColumnName("minor_units_factor").HasDefaultValue(100);
            cb.Property(c => c.DecimalPlaces).HasColumnName("currency_decimals").HasDefaultValue(2);
        });
    }
}
