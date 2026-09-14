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
