using System.Globalization;
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

        // 3. Apply schema upgrades if migrating an existing pre-Phase 5 or pre-Phase 6 SQLite database
        await UpgradeSchemaIfNeededAsync(context, cancellationToken);

        // 4. Seed default tax categories if empty (GST 0%, GST 5%, GST 18%, Exempt)
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

        // 5. Initial Store profile and first Owner are created atomically during POST /api/v1/auth/setup-owner
        // No hardcoded credentials or premature store seeding.

        // 6. Seed default invoice sequence if empty
        if (!await context.InvoiceSequences.AnyAsync(cancellationToken))
        {
            var seq = new InvoiceSequence("DEFAULT_SALE", "INV", 0);
            context.InvoiceSequences.Add(seq);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public static async Task UpgradeSchemaIfNeededAsync(RetailDbContext context, CancellationToken cancellationToken = default)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        using var cmd = connection.CreateCommand();

        // 1. Check if stock_balances table exists and if is_low_stock column is present
        cmd.CommandText = "PRAGMA table_info(stock_balances);";
        var hasIsLowStock = false;
        var tableExists = false;

        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tableExists = true;
                var colName = reader.GetString(1);
                if (string.Equals(colName, "is_low_stock", StringComparison.OrdinalIgnoreCase))
                {
                    hasIsLowStock = true;
                    break;
                }
            }
        }

        if (tableExists && !hasIsLowStock)
        {
            cmd.CommandText = "ALTER TABLE stock_balances ADD COLUMN is_low_stock INTEGER NOT NULL DEFAULT 0;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Backfill is_low_stock for existing stock balance rows
            var balancesToUpdate = new List<(Guid Id, int IsLow)>();
            cmd.CommandText = "SELECT id, current_quantity, reorder_level FROM stock_balances;";
            using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    var currentStr = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var reorderStr = reader.IsDBNull(2) ? null : reader.GetString(2);

                    var currentVal = ParseQuantityValue(currentStr);
                    var reorderVal = ParseQuantityValue(reorderStr);
                    var isLow = currentVal <= reorderVal ? 1 : 0;
                    balancesToUpdate.Add((id, isLow));
                }
            }

            foreach (var (id, isLow) in balancesToUpdate)
            {
                cmd.CommandText = $"UPDATE stock_balances SET is_low_stock = {isLow} WHERE id = '{id}' COLLATE NOCASE;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (tableExists)
        {
            // Ensure IX_stock_balances_is_low_stock index exists
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_stock_balances_is_low_stock ON stock_balances (is_low_stock);";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Ensure UK_categories_name index exists with NOCASE
        cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_categories_name ON categories (name COLLATE NOCASE);";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Ensure UK_brands_name index exists with NOCASE
        cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_brands_name ON brands (name COLLATE NOCASE);";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // --- Phase 6 Schema Upgrades ---

        // Check stores table for cash_rounding_enabled and time_zone_id
        cmd.CommandText = "PRAGMA table_info(stores);";
        var hasCashRounding = false;
        var hasTimeZone = false;
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var colName = reader.GetString(1);
                if (string.Equals(colName, "cash_rounding_enabled", StringComparison.OrdinalIgnoreCase))
                    hasCashRounding = true;
                if (string.Equals(colName, "time_zone_id", StringComparison.OrdinalIgnoreCase))
                    hasTimeZone = true;
            }
        }

        if (!hasCashRounding)
        {
            cmd.CommandText = "ALTER TABLE stores ADD COLUMN cash_rounding_enabled INTEGER NOT NULL DEFAULT 1;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!hasTimeZone)
        {
            cmd.CommandText = "ALTER TABLE stores ADD COLUMN time_zone_id TEXT NOT NULL DEFAULT 'India Standard Time';";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Ensure sale_item_tax_components table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS sale_item_tax_components (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sale_item_id INTEGER NOT NULL REFERENCES sales_items(id) ON DELETE RESTRICT,
    component_name TEXT(50) NOT NULL,
    rate_percentage TEXT NOT NULL,
    amount INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_sale_item_tax_components_sale_item_id ON sale_item_tax_components(sale_item_id);
CREATE UNIQUE INDEX IF NOT EXISTS UK_sale_item_tax_components_item_component ON sale_item_tax_components(sale_item_id, component_name);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Ensure sale_refunds table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS sale_refunds (
    id TEXT PRIMARY KEY,
    sale_id TEXT NOT NULL REFERENCES sales_invoices(id) ON DELETE RESTRICT,
    refund_number TEXT(50) NOT NULL,
    client_transaction_id TEXT NOT NULL,
    authorized_by_user_id TEXT NOT NULL REFERENCES users(id),
    authorized_by_user_name TEXT(100) NOT NULL,
    terminal_id TEXT NOT NULL REFERENCES terminals(id),
    refund_method INTEGER NOT NULL,
    total_refund_amount INTEGER NOT NULL,
    reason TEXT(250) NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UK_sale_refunds_refund_number ON sale_refunds(refund_number);
CREATE UNIQUE INDEX IF NOT EXISTS UK_sale_refunds_client_tx_id ON sale_refunds(client_transaction_id);
CREATE INDEX IF NOT EXISTS IX_sale_refunds_sale_id ON sale_refunds(sale_id);
CREATE INDEX IF NOT EXISTS IX_sale_refunds_authorized_by ON sale_refunds(authorized_by_user_id);
CREATE INDEX IF NOT EXISTS IX_sale_refunds_created_at ON sale_refunds(created_at_utc);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Ensure sale_refund_items table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS sale_refund_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    refund_id TEXT NOT NULL REFERENCES sale_refunds(id) ON DELETE CASCADE,
    sale_item_id INTEGER NOT NULL REFERENCES sales_items(id) ON DELETE RESTRICT,
    product_id TEXT NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    quantity_refunded TEXT(50) NOT NULL,
    refund_amount INTEGER NOT NULL,
    tax_refund_amount INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_sale_refund_items_refund_id ON sale_refund_items(refund_id);
CREATE INDEX IF NOT EXISTS IX_sale_refund_items_sale_item_id ON sale_refund_items(sale_item_id);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Check invoice_sequences table for period_date column
        cmd.CommandText = "PRAGMA table_info(invoice_sequences);";
        var hasPeriodDate = false;
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var colName = reader.GetString(1);
                if (string.Equals(colName, "period_date", StringComparison.OrdinalIgnoreCase))
                    hasPeriodDate = true;
            }
        }

        if (!hasPeriodDate)
        {
            cmd.CommandText = "ALTER TABLE invoice_sequences ADD COLUMN period_date TEXT(8) NOT NULL DEFAULT '';";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_invoice_sequences_name_period ON invoice_sequences(sequence_name, period_date);";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static decimal ParseQuantityValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        var parts = raw.Split('|');
        if (decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var val))
        {
            return val;
        }
        return 0m;
    }
}
