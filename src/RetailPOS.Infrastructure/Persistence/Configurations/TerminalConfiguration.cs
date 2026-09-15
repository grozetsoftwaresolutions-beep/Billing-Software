using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class TerminalConfiguration : IEntityTypeConfiguration<Terminal>
{
    public void Configure(EntityTypeBuilder<Terminal> builder)
    {
        builder.ToTable("terminals");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TerminalCode).HasMaxLength(50).IsRequired();
        builder.HasIndex(t => t.TerminalCode).IsUnique().HasDatabaseName("UK_terminals_terminal_code");

        builder.Property(t => t.TerminalName).HasMaxLength(100).IsRequired();
        builder.Property(t => t.ReceiptPrinterName).HasMaxLength(100);
        builder.Property(t => t.IsActive).IsRequired().HasDefaultValue(true);

        // Phase 12 Additions
        builder.Property(t => t.Status)
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(TerminalStatus.Active);

        builder.Property(t => t.PairingTokenHash)
            .HasMaxLength(64);

        builder.Property(t => t.MachineName)
            .HasMaxLength(100);

        builder.Property(t => t.ClientIpAddress)
            .HasMaxLength(50);

        builder.Property(t => t.LastHeartbeatUtc);
    }
}
