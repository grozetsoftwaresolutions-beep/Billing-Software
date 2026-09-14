using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class SaleRefundConfiguration : IEntityTypeConfiguration<SaleRefund>
{
    public void Configure(EntityTypeBuilder<SaleRefund> builder)
    {
        builder.ToTable("sale_refunds");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.SaleId).IsRequired();
        builder.HasIndex(r => r.SaleId).HasDatabaseName("IX_sale_refunds_sale_id");

        builder.Property(r => r.RefundNumber).HasMaxLength(50).IsRequired();
        builder.HasIndex(r => r.RefundNumber).IsUnique().HasDatabaseName("UK_sale_refunds_refund_number");

        builder.Property(r => r.ClientTransactionId).IsRequired();
        builder.HasIndex(r => r.ClientTransactionId).IsUnique().HasDatabaseName("UK_sale_refunds_client_tx_id");

        builder.Property(r => r.AuthorizedByUserId).IsRequired();
        builder.HasIndex(r => r.AuthorizedByUserId).HasDatabaseName("IX_sale_refunds_authorized_by");

        builder.Property(r => r.AuthorizedByUserName).HasMaxLength(100).IsRequired();
        builder.Property(r => r.TerminalId).IsRequired();
        builder.Property(r => r.RefundMethod).IsRequired();
        builder.Property(r => r.TotalRefundAmount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(250).IsRequired();

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.HasIndex(r => r.CreatedAtUtc).HasDatabaseName("IX_sale_refunds_created_at");

        builder.HasMany(r => r.Items)
            .WithOne()
            .HasForeignKey(i => i.RefundId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
