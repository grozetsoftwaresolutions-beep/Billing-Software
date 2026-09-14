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

        // 3. Apply schema upgrades if migrating an existing pre-Phase 5, pre-Phase 6, or pre-Phase 7 SQLite database
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

        // Ensure UK_categories_name index exists with NOCASE if categories table exists
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='categories';";
        if (await cmd.ExecuteScalarAsync(cancellationToken) != null)
        {
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_categories_name ON categories (name COLLATE NOCASE);";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Ensure UK_brands_name index exists with NOCASE if brands table exists
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='brands';";
        if (await cmd.ExecuteScalarAsync(cancellationToken) != null)
        {
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_brands_name ON brands (name COLLATE NOCASE);";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

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
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='invoice_sequences';";
        var hasInvoiceSeqTable = await cmd.ExecuteScalarAsync(cancellationToken) != null;
        if (hasInvoiceSeqTable)
        {
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

        // --- Phase 7 Schema Upgrades: Purchases & Suppliers ---

        // 1. Check suppliers table columns
        cmd.CommandText = "PRAGMA table_info(suppliers);";
        var supplierCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                supplierCols.Add(reader.GetString(1));
            }
        }

        if (supplierCols.Count > 0)
        {
            if (!supplierCols.Contains("Email"))
            {
                cmd.CommandText = "ALTER TABLE suppliers ADD COLUMN Email TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!supplierCols.Contains("Address"))
            {
                cmd.CommandText = "ALTER TABLE suppliers ADD COLUMN Address TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!supplierCols.Contains("PaymentTermsDays"))
            {
                cmd.CommandText = "ALTER TABLE suppliers ADD COLUMN PaymentTermsDays INTEGER NOT NULL DEFAULT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!supplierCols.Contains("CreditBalance"))
            {
                cmd.CommandText = "ALTER TABLE suppliers ADD COLUMN CreditBalance INTEGER NOT NULL DEFAULT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!supplierCols.Contains("Notes"))
            {
                cmd.CommandText = "ALTER TABLE suppliers ADD COLUMN Notes TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!supplierCols.Contains("CreatedAtUtc"))
            {
                cmd.CommandText = "ALTER TABLE suppliers ADD COLUMN CreatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!supplierCols.Contains("UpdatedAtUtc"))
            {
                cmd.CommandText = "ALTER TABLE suppliers ADD COLUMN UpdatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_suppliers_name ON suppliers (Name COLLATE NOCASE);";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2. Check purchase_invoices table columns
        cmd.CommandText = "PRAGMA table_info(purchase_invoices);";
        var purchaseCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                purchaseCols.Add(reader.GetString(1));
            }
        }

        if (purchaseCols.Count > 0)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM purchase_invoices;";
            var purchaseRowCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            int resolvedStoreId = 1;
            string? resolvedUserId = null;

            if (purchaseRowCount > 0)
            {
                cmd.CommandText = "SELECT Id FROM stores ORDER BY Id LIMIT 1;";
                var storeObj = await cmd.ExecuteScalarAsync(cancellationToken);
                if (storeObj == null)
                    throw new InvalidOperationException("Cannot migrate legacy purchase invoices: no valid store record exists in database.");
                resolvedStoreId = Convert.ToInt32(storeObj, CultureInfo.InvariantCulture);

                cmd.CommandText = "PRAGMA table_info(users);";
                var userCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                        userCols.Add(reader.GetString(1));
                }

                if (userCols.Contains("IsActive"))
                    cmd.CommandText = "SELECT Id FROM users WHERE IsActive = 1 LIMIT 1;";
                else if (userCols.Contains("Status"))
                    cmd.CommandText = "SELECT Id FROM users WHERE Status = 1 LIMIT 1;";
                else
                    cmd.CommandText = "SELECT Id FROM users LIMIT 1;";

                var userObj = await cmd.ExecuteScalarAsync(cancellationToken);
                if (userObj == null)
                {
                    cmd.CommandText = "SELECT Id FROM users LIMIT 1;";
                    userObj = await cmd.ExecuteScalarAsync(cancellationToken);
                }
                if (userObj == null)
                    throw new InvalidOperationException("Cannot migrate legacy purchase invoices: no valid user record exists in database.");
                resolvedUserId = userObj.ToString();
            }
            else
            {
                cmd.CommandText = "SELECT Id FROM stores ORDER BY Id LIMIT 1;";
                var storeObj = await cmd.ExecuteScalarAsync(cancellationToken);
                if (storeObj != null) resolvedStoreId = Convert.ToInt32(storeObj, CultureInfo.InvariantCulture);

                cmd.CommandText = "SELECT Id FROM users LIMIT 1;";
                var userObj = await cmd.ExecuteScalarAsync(cancellationToken);
                resolvedUserId = userObj?.ToString() ?? Guid.NewGuid().ToString();
            }

            if (!purchaseCols.Contains("StoreId"))
            {
                cmd.CommandText = $"ALTER TABLE purchase_invoices ADD COLUMN StoreId INTEGER NOT NULL DEFAULT {resolvedStoreId};";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("SupplierInvoiceNumber"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN SupplierInvoiceNumber TEXT NOT NULL DEFAULT '';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                cmd.CommandText = "UPDATE purchase_invoices SET SupplierInvoiceNumber = InvoiceNumber WHERE SupplierInvoiceNumber = '';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("InvoiceDate"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN InvoiceDate TEXT NOT NULL DEFAULT '2026-01-01';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("ReceivedDateUtc"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN ReceivedDateUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("CreditOffsetAmount"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN CreditOffsetAmount INTEGER NOT NULL DEFAULT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("AmountPaid"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN AmountPaid INTEGER NOT NULL DEFAULT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("BalanceDue"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN BalanceDue INTEGER NOT NULL DEFAULT 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                cmd.CommandText = "UPDATE purchase_invoices SET BalanceDue = GrandTotal WHERE BalanceDue = 0 AND GrandTotal > 0 AND AmountPaid = 0;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("PaymentStatus"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN PaymentStatus INTEGER NOT NULL DEFAULT 1;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("ClientTransactionId"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN ClientTransactionId TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("CancellationClientTransactionId"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN CancellationClientTransactionId TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("CancellationReason"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN CancellationReason TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("Notes"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN Notes TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("CreatedByUserId"))
            {
                cmd.CommandText = $"ALTER TABLE purchase_invoices ADD COLUMN CreatedByUserId TEXT NOT NULL DEFAULT '{resolvedUserId}';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("CreatedAtUtc"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN CreatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!purchaseCols.Contains("UpdatedAtUtc"))
            {
                cmd.CommandText = "ALTER TABLE purchase_invoices ADD COLUMN UpdatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_purchase_invoices_client_tx ON purchase_invoices (ClientTransactionId) WHERE ClientTransactionId IS NOT NULL;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UK_purchase_invoices_cancel_tx ON purchase_invoices (CancellationClientTransactionId) WHERE CancellationClientTransactionId IS NOT NULL;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3. Check purchase_items table columns
        cmd.CommandText = "PRAGMA table_info(purchase_items);";
        var itemCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                itemCols.Add(reader.GetString(1));
            }
        }

        if (itemCols.Count > 0)
        {
            if (!itemCols.Contains("ProductSku"))
            {
                cmd.CommandText = "ALTER TABLE purchase_items ADD COLUMN ProductSku TEXT NOT NULL DEFAULT '';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!itemCols.Contains("ProductName"))
            {
                cmd.CommandText = "ALTER TABLE purchase_items ADD COLUMN ProductName TEXT NOT NULL DEFAULT '';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!itemCols.Contains("UnitOfMeasure"))
            {
                cmd.CommandText = "ALTER TABLE purchase_items ADD COLUMN UnitOfMeasure TEXT NOT NULL DEFAULT 'PC';";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!itemCols.Contains("BatchNumber"))
            {
                cmd.CommandText = "ALTER TABLE purchase_items ADD COLUMN BatchNumber TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!itemCols.Contains("ExpiryDate"))
            {
                cmd.CommandText = "ALTER TABLE purchase_items ADD COLUMN ExpiryDate TEXT NULL;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Backfill snapshot columns for any items missing SKU or ProductName
            cmd.CommandText = @"
                UPDATE purchase_items 
                SET ProductSku = COALESCE((SELECT SKU FROM products WHERE products.Id = purchase_items.ProductId), 'LEGACY'),
                    ProductName = COALESCE((SELECT Name FROM products WHERE products.Id = purchase_items.ProductId), 'Legacy Item'),
                    UnitOfMeasure = COALESCE((SELECT UnitOfMeasure FROM products WHERE products.Id = purchase_items.ProductId), 'PC')
                WHERE ProductSku = '' OR ProductName = '';
            ";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 4. Ensure purchase_item_tax_components table exists with ON DELETE RESTRICT
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='purchase_item_tax_components';";
        var taxCompSql = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
        if (taxCompSql == null)
        {
            cmd.CommandText = @"
CREATE TABLE purchase_item_tax_components (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PurchaseItemId INTEGER NOT NULL REFERENCES purchase_items(Id) ON DELETE RESTRICT,
    ComponentName TEXT(50) NOT NULL,
    RatePercentage TEXT NOT NULL,
    TaxAmount INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_purchase_item_tax_components_item_id ON purchase_item_tax_components(PurchaseItemId);
CREATE UNIQUE INDEX IF NOT EXISTS UK_purchase_item_tax_components_item_component ON purchase_item_tax_components(PurchaseItemId, ComponentName);
";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (taxCompSql.Contains("CASCADE", StringComparison.OrdinalIgnoreCase))
        {
            cmd.CommandText = @"
CREATE TABLE purchase_item_tax_components_new (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PurchaseItemId INTEGER NOT NULL REFERENCES purchase_items(Id) ON DELETE RESTRICT,
    ComponentName TEXT(50) NOT NULL,
    RatePercentage TEXT NOT NULL,
    TaxAmount INTEGER NOT NULL
);
INSERT INTO purchase_item_tax_components_new (Id, PurchaseItemId, ComponentName, RatePercentage, TaxAmount)
SELECT Id, PurchaseItemId, ComponentName, RatePercentage, TaxAmount FROM purchase_item_tax_components;
DROP TABLE purchase_item_tax_components;
ALTER TABLE purchase_item_tax_components_new RENAME TO purchase_item_tax_components;
CREATE INDEX IF NOT EXISTS IX_purchase_item_tax_components_item_id ON purchase_item_tax_components(PurchaseItemId);
CREATE UNIQUE INDEX IF NOT EXISTS UK_purchase_item_tax_components_item_component ON purchase_item_tax_components(PurchaseItemId, ComponentName);
";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 5. Ensure supplier_payments table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS supplier_payments (
    Id TEXT PRIMARY KEY,
    StoreId INTEGER NOT NULL REFERENCES stores(Id),
    SupplierId TEXT NOT NULL REFERENCES suppliers(Id),
    Type INTEGER NOT NULL,
    Amount INTEGER NOT NULL,
    Method INTEGER NOT NULL,
    ReferenceNumber TEXT(50) NULL,
    PaymentDateUtc TEXT NOT NULL,
    ClientTransactionId TEXT NOT NULL,
    CreatedByUserId TEXT NOT NULL REFERENCES users(Id),
    Notes TEXT(500) NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UK_supplier_payments_client_tx ON supplier_payments(ClientTransactionId);
CREATE INDEX IF NOT EXISTS IX_supplier_payments_supplier_id ON supplier_payments(SupplierId);
CREATE INDEX IF NOT EXISTS IX_supplier_payments_payment_date ON supplier_payments(PaymentDateUtc);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // 6. Ensure supplier_payment_allocations table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS supplier_payment_allocations (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SupplierPaymentId TEXT NOT NULL REFERENCES supplier_payments(Id) ON DELETE CASCADE,
    PurchaseInvoiceId TEXT NOT NULL REFERENCES purchase_invoices(Id) ON DELETE RESTRICT,
    AmountAllocated INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_supplier_payment_allocations_payment_id ON supplier_payment_allocations(SupplierPaymentId);
CREATE INDEX IF NOT EXISTS IX_supplier_payment_allocations_invoice_id ON supplier_payment_allocations(PurchaseInvoiceId);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // -------------------------------------------------------------
        // Phase 8: Customers, Customer Payments, Cash Sessions & Transactions
        // -------------------------------------------------------------

        // 7. Ensure customers table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS customers (
    Id TEXT PRIMARY KEY,
    Name TEXT(150) NOT NULL,
    Phone TEXT(20) NULL,
    Email TEXT(100) NULL,
    Address TEXT(300) NULL,
    Gstin TEXT(15) NULL,
    OutstandingCredit INTEGER NOT NULL DEFAULT 0,
    CreditBalance INTEGER NOT NULL DEFAULT 0,
    CreditLimit INTEGER NOT NULL DEFAULT 0,
    IsActive INTEGER NOT NULL DEFAULT 1,
    Notes TEXT(500) NULL,
    CreatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z',
    UpdatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z'
);
CREATE INDEX IF NOT EXISTS IX_customers_name ON customers(Name);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Check if existing customers table has any missing Phase 8 columns
        cmd.CommandText = "PRAGMA table_info(customers);";
        var custColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                custColumns.Add(reader.GetString(1));
            }
        }

        if (!custColumns.Contains("Address"))
        {
            cmd.CommandText = "ALTER TABLE customers ADD COLUMN Address TEXT(300) NULL;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!custColumns.Contains("Gstin"))
        {
            cmd.CommandText = "ALTER TABLE customers ADD COLUMN Gstin TEXT(15) NULL;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!custColumns.Contains("CreditBalance"))
        {
            cmd.CommandText = "ALTER TABLE customers ADD COLUMN CreditBalance INTEGER NOT NULL DEFAULT 0;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!custColumns.Contains("Notes"))
        {
            cmd.CommandText = "ALTER TABLE customers ADD COLUMN Notes TEXT(500) NULL;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!custColumns.Contains("CreatedAtUtc"))
        {
            cmd.CommandText = "ALTER TABLE customers ADD COLUMN CreatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z';";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!custColumns.Contains("UpdatedAtUtc"))
        {
            cmd.CommandText = "ALTER TABLE customers ADD COLUMN UpdatedAtUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00Z';";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Now that all columns are guaranteed to exist, ensure indexes
        cmd.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS UK_customers_phone ON customers(Phone) WHERE Phone IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_customers_gstin ON customers(Gstin) WHERE Gstin IS NOT NULL;
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // 8. Ensure customer_payments table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS customer_payments (
    Id TEXT PRIMARY KEY,
    StoreId INTEGER NOT NULL REFERENCES stores(Id),
    CustomerId TEXT NOT NULL REFERENCES customers(Id) ON DELETE RESTRICT,
    Amount INTEGER NOT NULL,
    Method INTEGER NOT NULL,
    ReferenceNumber TEXT(50) NULL,
    PaymentDateUtc TEXT NOT NULL,
    ClientTransactionId TEXT NOT NULL,
    CreatedByUserId TEXT NOT NULL REFERENCES users(Id),
    Notes TEXT(500) NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_customer_payments_customer_id ON customer_payments(CustomerId);
CREATE UNIQUE INDEX IF NOT EXISTS UK_customer_payments_client_tx ON customer_payments(ClientTransactionId);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // 9. Ensure cash_sessions table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS cash_sessions (
    Id TEXT PRIMARY KEY,
    StoreId INTEGER NOT NULL REFERENCES stores(Id),
    TerminalId TEXT NOT NULL,
    CashierUserId TEXT NOT NULL,
    OpeningTimeUtc TEXT NOT NULL,
    ClosingTimeUtc TEXT NULL,
    OpeningFloat INTEGER NOT NULL,
    TotalCashSales INTEGER NOT NULL DEFAULT 0,
    TotalCashRepayments INTEGER NOT NULL DEFAULT 0,
    TotalCashRefunds INTEGER NOT NULL DEFAULT 0,
    TotalPayIns INTEGER NOT NULL DEFAULT 0,
    TotalPayOuts INTEGER NOT NULL DEFAULT 0,
    TotalCashDrops INTEGER NOT NULL DEFAULT 0,
    ClosingCash INTEGER NULL,
    Discrepancy INTEGER NULL,
    Status INTEGER NOT NULL DEFAULT 1,
    ClosingNotes TEXT(500) NULL,
    IsForceClosed INTEGER NOT NULL DEFAULT 0,
    ClosedByUserId TEXT NULL,
    ForceCloseReason TEXT(500) NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UK_cash_sessions_open_terminal ON cash_sessions(TerminalId) WHERE Status = 1;
CREATE INDEX IF NOT EXISTS IX_cash_sessions_terminal_id ON cash_sessions(TerminalId);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // 10. Ensure cash_session_transactions table exists
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS cash_session_transactions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CashSessionId TEXT NOT NULL REFERENCES cash_sessions(Id) ON DELETE CASCADE,
    Type INTEGER NOT NULL,
    Amount INTEGER NOT NULL,
    Reason TEXT(250) NOT NULL,
    ClientTransactionId TEXT NOT NULL,
    CreatedByUserId TEXT NOT NULL,
    AuthorizedByUserId TEXT NULL,
    SaleInvoiceId TEXT NULL REFERENCES sales_invoices(Id) ON DELETE RESTRICT,
    RefundId TEXT NULL REFERENCES sale_refunds(Id) ON DELETE RESTRICT,
    CustomerPaymentId TEXT NULL REFERENCES customer_payments(Id) ON DELETE RESTRICT,
    TimestampUtc TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_cash_session_transactions_session_id ON cash_session_transactions(CashSessionId);
CREATE UNIQUE INDEX IF NOT EXISTS UK_cash_session_transactions_client_tx ON cash_session_transactions(ClientTransactionId);
CREATE INDEX IF NOT EXISTS IX_cash_session_transactions_sale_id ON cash_session_transactions(SaleInvoiceId) WHERE SaleInvoiceId IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_cash_session_transactions_refund_id ON cash_session_transactions(RefundId) WHERE RefundId IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_cash_session_transactions_payment_id ON cash_session_transactions(CustomerPaymentId) WHERE CustomerPaymentId IS NOT NULL;
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

                // Check if existing cash_session_transactions table has provenance columns
        cmd.CommandText = "PRAGMA table_info(cash_session_transactions);";
        var txColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                txColumns.Add(reader.GetString(1));
            }
        }

        if (txColumns.Count > 0)
        {
            if (!txColumns.Contains("SaleInvoiceId"))
            {
                cmd.CommandText = "ALTER TABLE cash_session_transactions ADD COLUMN SaleInvoiceId TEXT NULL REFERENCES sales_invoices(Id) ON DELETE RESTRICT;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!txColumns.Contains("RefundId"))
            {
                cmd.CommandText = "ALTER TABLE cash_session_transactions ADD COLUMN RefundId TEXT NULL REFERENCES sale_refunds(Id) ON DELETE RESTRICT;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!txColumns.Contains("CustomerPaymentId"))
            {
                cmd.CommandText = "ALTER TABLE cash_session_transactions ADD COLUMN CustomerPaymentId TEXT NULL REFERENCES customer_payments(Id) ON DELETE RESTRICT;";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS IX_cash_session_transactions_sale_id ON cash_session_transactions(SaleInvoiceId) WHERE SaleInvoiceId IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_cash_session_transactions_refund_id ON cash_session_transactions(RefundId) WHERE RefundId IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_cash_session_transactions_payment_id ON cash_session_transactions(CustomerPaymentId) WHERE CustomerPaymentId IS NOT NULL;
";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 11. PRAGMA integrity and foreign key checks
        cmd.CommandText = "PRAGMA foreign_key_check;";
        using (var fkReader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await fkReader.ReadAsync(cancellationToken))
            {
                var fkViolationTable = fkReader.GetString(0);
                throw new InvalidOperationException($"Foreign key integrity violation detected in table '{fkViolationTable}' during Phase 8 migration.");
            }
        }

        cmd.CommandText = "PRAGMA integrity_check;";
        var integrityResult = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Database integrity check failed: {integrityResult}");
        }
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
