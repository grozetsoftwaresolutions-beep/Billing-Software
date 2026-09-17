using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Terminals;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class PairingEnrollmentTokenConfiguration : IEntityTypeConfiguration<PairingEnrollmentToken>
{
    public void Configure(EntityTypeBuilder<PairingEnrollmentToken> builder)
    {
        builder.ToTable("TerminalEnrollmentTokens");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("PairingOperationId");
        builder.Property(t => t.StoreId).IsRequired();
        builder.Property(t => t.TerminalId).IsRequired();
        builder.Property(t => t.Salt).IsRequired();
        builder.Property(t => t.Verifier).IsRequired();
        builder.Property(t => t.ClientEphemeralA);
        builder.Property(t => t.ServerEphemeralB);
        builder.Property(t => t.State).IsRequired().HasMaxLength(50);
        builder.Property(t => t.RetryAuthHash);
        builder.Property(t => t.EncryptedTmkBlob);
        builder.Property(t => t.ServerEvidenceM2);
        builder.Property(t => t.FailedAttempts).IsRequired().HasDefaultValue(0);
        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.ExpiresAtUtc).IsRequired();
        builder.Property(t => t.ProvisionedAtUtc);
        builder.Property(t => t.CompletedAtUtc);
        builder.Property(t => t.RevocationReason);
    }
}
