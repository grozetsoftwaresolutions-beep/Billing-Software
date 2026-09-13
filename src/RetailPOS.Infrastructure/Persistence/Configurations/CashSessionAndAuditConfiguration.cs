using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Infrastructure.Persistence.Converters;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable("cash_sessions");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TerminalId).IsRequired();
        builder.HasIndex(c => c.TerminalId).HasDatabaseName("IX_cash_sessions_terminal_id");

        builder.Property(c => c.CashierId).IsRequired();
        builder.HasIndex(c => c.CashierId).HasDatabaseName("IX_cash_sessions_cashier_id");

        builder.Property(c => c.OpenedAtUtc).IsRequired();
        builder.Property(c => c.OpeningFloat).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER").IsRequired();
        builder.Property(c => c.ClosingCashCounted).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER");
        builder.Property(c => c.ExpectedClosingCash).HasConversion<MoneyToMinorUnitsConverter>().HasColumnType("INTEGER");

        builder.Property(c => c.Status).IsRequired();
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.HasIndex(a => a.Action).HasDatabaseName("IX_audit_logs_action");

        builder.Property(a => a.EntityName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.Details).HasMaxLength(1000);
        builder.Property(a => a.Success).IsRequired().HasDefaultValue(true);

        builder.Property(a => a.UserId);
        builder.HasIndex(a => a.UserId).HasDatabaseName("IX_audit_logs_user_id");

        builder.Property(a => a.TerminalId);
        builder.HasIndex(a => a.TerminalId).HasDatabaseName("IX_audit_logs_terminal_id");

        builder.Property(a => a.CreatedAtUtc).IsRequired();
        builder.HasIndex(a => a.CreatedAtUtc).HasDatabaseName("IX_audit_logs_created_at");
    }
}
