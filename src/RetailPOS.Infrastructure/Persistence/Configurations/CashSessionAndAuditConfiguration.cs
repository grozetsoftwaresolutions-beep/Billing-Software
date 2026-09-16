using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Audit;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.HasIndex(a => a.Action).HasDatabaseName("IX_audit_logs_action");
        builder.HasIndex(a => new { a.Action, a.EntityId }).HasDatabaseName("IX_audit_logs_database_restored").HasFilter("Action = 'DATABASE_RESTORED'").IsUnique();

        builder.Property(a => a.EntityName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.Details).HasMaxLength(1000);
        builder.Property(a => a.Success).IsRequired().HasDefaultValue(true);

        builder.Property(a => a.UserId);
        builder.HasIndex(a => a.UserId).HasDatabaseName("IX_audit_logs_user_id");

        builder.Property(a => a.UserName).HasMaxLength(100);
        builder.HasIndex(a => a.UserName).HasDatabaseName("IX_audit_logs_user_name");

        builder.Property(a => a.TerminalId);
        builder.HasIndex(a => a.TerminalId).HasDatabaseName("IX_audit_logs_terminal_id");

        builder.Property(a => a.Reason).HasMaxLength(500);
        builder.Property(a => a.BeforeJson).HasMaxLength(4000);
        builder.Property(a => a.AfterJson).HasMaxLength(4000);
        builder.Property(a => a.IpAddress).HasMaxLength(50);
        builder.Property(a => a.CorrelationId).HasMaxLength(100);
        builder.HasIndex(a => a.CorrelationId).HasDatabaseName("IX_audit_logs_correlation_id");

        // Failsafe recovery identifiers
        builder.Property(a => a.FailsafeInstanceId);
        builder.Property(a => a.ClearOperationId);
        builder.Property(a => a.TargetSettingsSha256).HasMaxLength(64);
        builder.HasIndex(a => new { a.FailsafeInstanceId, a.ClearOperationId }).HasDatabaseName("IX_audit_logs_failsafe_recovery");
        builder.HasIndex(a => new { a.Action, a.FailsafeInstanceId, a.ClearOperationId })
            .HasDatabaseName("UQ_audit_logs_failsafe_clear")
            .HasFilter("Action = 'SECURITY_SETTINGS_FAILSAFE_CLEARED' AND ClearOperationId IS NOT NULL")
            .IsUnique();

        builder.Property(a => a.CreatedAtUtc).IsRequired();
        builder.HasIndex(a => a.CreatedAtUtc).HasDatabaseName("IX_audit_logs_created_at");
    }
}
