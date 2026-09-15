namespace RetailPOS.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Backup;

public sealed class BackupRecordConfiguration : IEntityTypeConfiguration<BackupRecord>
{
    public void Configure(EntityTypeBuilder<BackupRecord> builder)
    {
        builder.ToTable("backup_history");
        builder.HasKey(b => b.BackupOperationId);

        builder.Property(b => b.BackupOperationId).ValueGeneratedNever();
        builder.Property(b => b.FileName).HasMaxLength(255).IsRequired();
        builder.HasIndex(b => b.FileName).IsUnique().HasDatabaseName("UK_backup_history_filename");

        builder.Property(b => b.FilePath).HasMaxLength(1000).IsRequired();
        builder.Property(b => b.SizeBytes).IsRequired();
        builder.Property(b => b.DatabaseSha256).HasMaxLength(64).IsRequired();
        builder.Property(b => b.Type).IsRequired();
        builder.HasIndex(b => b.Type).HasDatabaseName("IX_backup_history_type");

        builder.Property(b => b.Status).IsRequired();
        builder.HasIndex(b => b.Status).HasDatabaseName("IX_backup_history_status");

        builder.Property(b => b.FileState).IsRequired();
        builder.HasIndex(b => b.FileState).HasDatabaseName("IX_backup_history_file_state");

        builder.Property(b => b.Origin).IsRequired();

        builder.Property(b => b.StoreId).IsRequired();
        builder.Property(b => b.StoreName).HasMaxLength(255).IsRequired();
        builder.Property(b => b.AppVersion).HasMaxLength(50).IsRequired();
        builder.Property(b => b.SchemaVersion).IsRequired();

        builder.Property(b => b.CreatedAtUtc).IsRequired();
        builder.HasIndex(b => b.CreatedAtUtc).HasDatabaseName("IX_backup_history_created_at");

        builder.Property(b => b.CompletedAtUtc);
        builder.Property(b => b.ErrorMessage).HasMaxLength(2000);
        builder.Property(b => b.TriggeredByUserId).HasMaxLength(100).IsRequired();
        builder.Property(b => b.TriggeredByUserName).HasMaxLength(100).IsRequired();
    }
}
