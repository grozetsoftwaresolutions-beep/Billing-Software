using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable("cash_sessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.StoreId).IsRequired();
        builder.Property(s => s.TerminalId).IsRequired();
        builder.Property(s => s.CashierUserId).IsRequired();
        builder.Property(s => s.OpeningTimeUtc).IsRequired();
        builder.Property(s => s.ClosingTimeUtc);

        builder.Property(s => s.OpeningFloat).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalCashSales).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalCashRepayments).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalCashRefunds).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalPayIns).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalPayOuts).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(s => s.TotalCashDrops).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();

        builder.Property(s => s.ClosingCash)
            .HasConversion(
                m => m.HasValue ? m.Value.MinorUnits : (long?)null,
                u => u.HasValue ? Money.FromMinorUnits(u.Value, Currency.INR) : null)
            .HasColumnType("INTEGER");

        builder.Property(s => s.Discrepancy)
            .HasConversion(
                m => m.HasValue ? m.Value.MinorUnits : (long?)null,
                u => u.HasValue ? Money.FromMinorUnits(u.Value, Currency.INR) : null)
            .HasColumnType("INTEGER");

        builder.Property(s => s.Status).IsRequired();
        builder.Property(s => s.ClosingNotes).HasMaxLength(500);
        builder.Property(s => s.IsForceClosed).IsRequired().HasDefaultValue(false);
        builder.Property(s => s.ClosedByUserId);
        builder.Property(s => s.ForceCloseReason).HasMaxLength(500);

        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsRequired();

        // Partial unique index: strictly one open session per terminal
        builder.HasIndex(s => s.TerminalId)
            .IsUnique()
            .HasFilter("Status = 1")
            .HasDatabaseName("UK_cash_sessions_open_terminal");
    }
}

public sealed class CashSessionTransactionConfiguration : IEntityTypeConfiguration<CashSessionTransaction>
{
    public void Configure(EntityTypeBuilder<CashSessionTransaction> builder)
    {
        builder.ToTable("cash_session_transactions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedOnAdd();

        builder.Property(t => t.CashSessionId).IsRequired();
        builder.HasIndex(t => t.CashSessionId).HasDatabaseName("IX_cash_session_transactions_session_id");

        builder.Property(t => t.Type).IsRequired();
        builder.Property(t => t.Amount).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(t => t.Reason).HasMaxLength(250).IsRequired();

        builder.Property(t => t.ClientTransactionId).IsRequired();
        builder.HasIndex(t => t.ClientTransactionId).IsUnique().HasDatabaseName("UK_cash_session_transactions_client_tx");

        builder.Property(t => t.CreatedByUserId).IsRequired();
        builder.Property(t => t.AuthorizedByUserId);
        builder.Property(t => t.TimestampUtc).IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc).IsRequired();

        builder.HasOne<CashSession>()
            .WithMany()
            .HasForeignKey(t => t.CashSessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
