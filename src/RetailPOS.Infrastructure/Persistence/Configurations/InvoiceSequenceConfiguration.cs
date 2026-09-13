using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailPOS.Domain.Entities.Configuration;

namespace RetailPOS.Infrastructure.Persistence.Configurations;

public sealed class InvoiceSequenceConfiguration : IEntityTypeConfiguration<InvoiceSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceSequence> builder)
    {
        builder.ToTable("invoice_sequences");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.SequenceName).HasMaxLength(50).IsRequired();
        builder.Property(s => s.Prefix).HasMaxLength(20).IsRequired();
        builder.Property(s => s.CurrentValue).IsRequired();
    }
}
