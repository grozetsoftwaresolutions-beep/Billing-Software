using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Hardware;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class TerminalHardwareConfigConfiguration : IEntityTypeConfiguration<TerminalHardwareConfig>
{
    public void Configure(EntityTypeBuilder<TerminalHardwareConfig> builder)
    {
        builder.ToTable("terminal_hardware_configs");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("TerminalId");

        builder.Property(c => c.PrinterNameOrAddress).HasMaxLength(255).IsRequired();
        builder.Property(c => c.ScalePortName).HasMaxLength(50);

        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc).IsRequired();

        builder.HasIndex(c => c.Id).HasDatabaseName("IX_terminal_hardware_configs_terminal_id");
    }
}
