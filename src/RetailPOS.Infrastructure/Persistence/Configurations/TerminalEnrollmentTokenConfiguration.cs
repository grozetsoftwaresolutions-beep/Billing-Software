using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class TerminalEnrollmentTokenConfiguration : IEntityTypeConfiguration<TerminalEnrollmentToken>
{
    public void Configure(EntityTypeBuilder<TerminalEnrollmentToken> builder)
    {
        builder.ToTable("terminal_enrollment_tokens");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("IX_terminal_enrollment_tokens_hash");

        builder.Property(t => t.TokenPurpose).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(t => t.BoundTerminalId);
        builder.HasIndex(t => t.BoundTerminalId).HasDatabaseName("IX_terminal_enrollment_tokens_bound_terminal");

        builder.Property(t => t.BoundServerId).HasMaxLength(50);
        builder.Property(t => t.BoundServerFingerprint).HasMaxLength(64);

        builder.Property(t => t.ExpiresAtUtc).IsRequired();
        builder.Property(t => t.IsConsumed).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.ConsumedAtUtc);
        builder.Property(t => t.IsRevoked).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.RevokedAtUtc);
        builder.Property(t => t.CreatedByUserId).HasMaxLength(100).IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc).IsRequired();
    }
}
