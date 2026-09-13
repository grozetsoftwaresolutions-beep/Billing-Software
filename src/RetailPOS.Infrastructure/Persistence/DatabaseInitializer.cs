using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Taxes;

namespace RetailPOS.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(RetailDbContext context, CancellationToken cancellationToken = default)
    {
        // 1. Ensure SQLite database schema is created
        await context.Database.EnsureCreatedAsync(cancellationToken);

        // 2. Apply SQLite runtime pragmas (WAL, foreign keys, busy timeout)
        SqliteConnectionConfigurator.ApplyPragmas(context);

        // 3. Seed default tax categories if empty (GST 0%, GST 5%, GST 18%, Exempt)
        if (!await context.TaxCategories.AnyAsync(cancellationToken))
        {
            var gst0 = new TaxCategory(1, "GST_0", "GST 0%", false, [new TaxComponent("Zero Tax", 0m)]);
            var gst5 = new TaxCategory(2, "GST_5", "GST 5%", false, [
                new TaxComponent("CGST", 2.5m),
                new TaxComponent("SGST", 2.5m)
            ]);
            var gst18 = new TaxCategory(3, "GST_18", "GST 18%", false, [
                new TaxComponent("CGST", 9.0m),
                new TaxComponent("SGST", 9.0m)
            ]);
            var exempt = new TaxCategory(4, "EXEMPT", "Tax Exempt", true);

            context.TaxCategories.AddRange(gst0, gst5, gst18, exempt);
        }

        // 4. Initial Store profile and first Owner are created atomically during POST /api/v1/auth/setup-owner
        // No hardcoded credentials or premature store seeding.

        // 5. Seed default invoice sequence if empty
        if (!await context.InvoiceSequences.AnyAsync(cancellationToken))
        {
            var seq = new InvoiceSequence("DEFAULT_SALE", "INV", 0);
            context.InvoiceSequences.Add(seq);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
