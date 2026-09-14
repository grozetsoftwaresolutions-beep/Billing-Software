using System.Globalization;
using System.Text;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Hardware;

public sealed record ReceiptStoreInfo(
    string StoreName,
    string? Address,
    string? Phone,
    string? TaxRegistrationNumber,
    string? FooterMessage);

public sealed record ReceiptInvoiceInfo(
    string InvoiceNumber,
    DateTime CreatedAtUtc,
    string CashierName,
    string TerminalCode,
    string? CustomerName = null,
    string? CustomerPhone = null,
    long? CustomerOutstandingBalancePaise = null);

public sealed record ReceiptLineItem(
    string ProductName,
    decimal Quantity,
    string UnitCode,
    long UnitPricePaise,
    long DiscountPaise,
    long TotalPaise);

public sealed record ReceiptTaxLine(
    string ComponentName,
    decimal RatePercentage,
    long TaxAmountPaise);

public sealed record ReceiptPaymentLine(
    PaymentMethod Method,
    long AmountPaise,
    long TenderedPaise = 0,
    long ChangePaise = 0);

public sealed record ReceiptFinancialSummary(
    long GrossSubtotalPaise,
    long LineDiscountsPaise,
    long InvoiceDiscountPaise,
    long TaxableSubtotalPaise,
    long TotalTaxPaise,
    long RoundingPaise,
    long FinalPayablePaise,
    IReadOnlyList<ReceiptLineItem> Items,
    IReadOnlyList<ReceiptTaxLine> Taxes,
    IReadOnlyList<ReceiptPaymentLine> Payments);

public sealed record ReceiptRenderOptions(
    PaperWidth PaperWidth = PaperWidth.Width80mm,
    PrinterEncoding Encoding = PrinterEncoding.Ascii,
    int CodePage = 0,
    bool AutoCut = true,
    bool OpenDrawer = false,
    bool IsReprint = false,
    int ReprintCount = 0,
    DateTime? LastReprintedAtUtc = null,
    string? TimeZoneId = "India Standard Time");

public static class ReceiptLayoutEngine
{
    public static EscPosBuilder BuildReceipt(
        ReceiptStoreInfo store,
        ReceiptInvoiceInfo invoice,
        ReceiptFinancialSummary summary,
        ReceiptRenderOptions options)
    {
        var builder = new EscPosBuilder(options.Encoding, options.CodePage);
        builder.Initialize();

        var totalWidth = options.PaperWidth == PaperWidth.Width58mm ? 32 : 48;
        var separator = new string('-', totalWidth);
        var doubleSeparator = new string('=', totalWidth);

        // 1. Store Header (Centered)
        builder.AlignCenter();
        builder.TextDoubleBoth().Bold(true).WriteLine(store.StoreName).TextNormal().Bold(false);
        if (!string.IsNullOrWhiteSpace(store.Address))
            builder.WriteLine(store.Address.Trim());
        if (!string.IsNullOrWhiteSpace(store.Phone))
            builder.WriteLine($"Phone: {store.Phone.Trim()}");
        if (!string.IsNullOrWhiteSpace(store.TaxRegistrationNumber))
            builder.WriteLine($"GSTIN: {store.TaxRegistrationNumber.Trim()}");

        // 2. Reprint Watermark (if applicable)
        if (options.IsReprint)
        {
            builder.WriteLine();
            builder.Bold(true).TextDoubleHeight().WriteLine("*** DUPLICATE / REPRINT ***").TextNormal().Bold(false);
            var reprintTime = FormatDateTime(options.LastReprintedAtUtc ?? DateTime.UtcNow, options.TimeZoneId);
            builder.WriteLine($"Reprint #{options.ReprintCount} at {reprintTime}");
        }

        builder.WriteLine(separator);

        // 3. Invoice Metadata (Left-aligned)
        builder.AlignLeft();
        builder.WriteLine($"Invoice : {invoice.InvoiceNumber}");
        builder.WriteLine($"Date    : {FormatDateTime(invoice.CreatedAtUtc, options.TimeZoneId)}");
        builder.WriteLine($"Counter : {invoice.TerminalCode} | Cashier: {invoice.CashierName}");
        if (!string.IsNullOrWhiteSpace(invoice.CustomerName))
        {
            var custPhone = string.IsNullOrWhiteSpace(invoice.CustomerPhone) ? "" : $" ({invoice.CustomerPhone})";
            builder.WriteLine($"Customer: {invoice.CustomerName}{custPhone}");
        }

        builder.WriteLine(separator);

        // 4. Line Items Table
        builder.Bold(true);
        if (options.PaperWidth == PaperWidth.Width58mm)
        {
            // 32 columns: Item (14) + Qty (5) + Rate (6) + Amt (7) = 32
            builder.WriteLine(FormatRow("Item", "Qty", "Rate", "Amt", [14, 5, 6, 7]));
        }
        else
        {
            // 48 columns: Item (22) + Qty (6) + Rate (9) + Amount (11) = 48
            builder.WriteLine(FormatRow("Item Description", "Qty", "Rate", "Amount", [22, 6, 9, 11]));
        }
        builder.Bold(false);
        builder.WriteLine(separator);

        foreach (var item in summary.Items)
        {
            var rateStr = FormatMoney(item.UnitPricePaise);
            var amtStr = FormatMoney(item.TotalPaise);
            var qtyStr = item.Quantity.ToString("0.###", CultureInfo.InvariantCulture);

            if (options.PaperWidth == PaperWidth.Width58mm)
            {
                var wrapped = WrapText(item.ProductName, 14);
                builder.WriteLine(FormatRow(wrapped[0], qtyStr, rateStr, amtStr, [14, 5, 6, 7]));
                for (int w = 1; w < wrapped.Count; w++)
                {
                    builder.WriteLine(FormatRow(wrapped[w], "", "", "", [14, 5, 6, 7]));
                }
            }
            else
            {
                var wrapped = WrapText(item.ProductName, 22);
                builder.WriteLine(FormatRow(wrapped[0], qtyStr, rateStr, amtStr, [22, 6, 9, 11]));
                for (int w = 1; w < wrapped.Count; w++)
                {
                    builder.WriteLine(FormatRow(wrapped[w], "", "", "", [22, 6, 9, 11]));
                }
            }

            if (item.DiscountPaise > 0)
            {
                builder.WriteLine($"  Disc: -{FormatMoney(item.DiscountPaise)}");
            }
        }

        builder.WriteLine(separator);

        // 5. Financial Summary
        builder.WriteLine(FormatSummaryLine("Gross Subtotal", FormatMoney(summary.GrossSubtotalPaise), totalWidth));
        if (summary.LineDiscountsPaise > 0)
            builder.WriteLine(FormatSummaryLine("Item Discounts", $"-{FormatMoney(summary.LineDiscountsPaise)}", totalWidth));
        if (summary.InvoiceDiscountPaise > 0)
            builder.WriteLine(FormatSummaryLine("Invoice Discount", $"-{FormatMoney(summary.InvoiceDiscountPaise)}", totalWidth));
        builder.WriteLine(FormatSummaryLine("Taxable Amount", FormatMoney(summary.TaxableSubtotalPaise), totalWidth));
        builder.WriteLine(FormatSummaryLine("Total Tax", FormatMoney(summary.TotalTaxPaise), totalWidth));
        if (summary.RoundingPaise != 0)
        {
            var roundStr = (summary.RoundingPaise > 0 ? "+" : "") + FormatMoney(summary.RoundingPaise);
            builder.WriteLine(FormatSummaryLine("Rounding Adj", roundStr, totalWidth));
        }

        builder.WriteLine(doubleSeparator);
        builder.Bold(true).TextDoubleHeight();
        builder.WriteLine(FormatSummaryLine("TOTAL PAYABLE", FormatMoney(summary.FinalPayablePaise), totalWidth));
        builder.TextNormal().Bold(false);
        builder.WriteLine(doubleSeparator);

        // 6. GST Tax Component Breakdown
        if (summary.Taxes.Count > 0)
        {
            builder.WriteLine("TAX BREAKDOWN:");
            foreach (var tax in summary.Taxes)
            {
                var taxRateStr = tax.RatePercentage.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                var label = $"  {tax.ComponentName} ({taxRateStr})";
                builder.WriteLine(FormatSummaryLine(label, FormatMoney(tax.TaxAmountPaise), totalWidth));
            }
            builder.WriteLine(separator);
        }

        // 7. Payment Tender Details
        builder.WriteLine("PAYMENT / TENDER:");
        foreach (var pay in summary.Payments)
        {
            var methodLabel = $"  {pay.Method}";
            builder.WriteLine(FormatSummaryLine(methodLabel, FormatMoney(pay.AmountPaise), totalWidth));
            if (pay.TenderedPaise > pay.AmountPaise)
            {
                builder.WriteLine(FormatSummaryLine("    Tendered", FormatMoney(pay.TenderedPaise), totalWidth));
                builder.WriteLine(FormatSummaryLine("    Change Given", FormatMoney(pay.ChangePaise), totalWidth));
            }
        }

        if (invoice.CustomerOutstandingBalancePaise.HasValue)
        {
            builder.WriteLine(separator);
            builder.Bold(true);
            builder.WriteLine(FormatSummaryLine("Customer Balance Due", FormatMoney(invoice.CustomerOutstandingBalancePaise.Value), totalWidth));
            builder.Bold(false);
        }

        // 8. Footer
        builder.WriteLine(separator);
        builder.AlignCenter();
        if (!string.IsNullOrWhiteSpace(store.FooterMessage))
        {
            var footerLines = WrapText(store.FooterMessage.Trim(), totalWidth);
            foreach (var line in footerLines)
                builder.WriteLine(line);
        }
        builder.WriteLine("Thank you for shopping with us!");
        builder.WriteLine("Powered by Offline Retail POS");

        // 9. Cut & Drawer
        builder.FeedLines(3);
        if (options.OpenDrawer)
        {
            builder.KickDrawer(0);
        }
        if (options.AutoCut)
        {
            builder.Cut(partial: true);
        }

        return builder;
    }

    public static EscPosBuilder BuildDiagnosticTestReceipt(
        ReceiptStoreInfo store,
        string terminalCode,
        string printerName,
        PaperWidth paperWidth,
        PrinterEncoding encoding,
        int codePage,
        bool openDrawer)
    {
        var builder = new EscPosBuilder(encoding, codePage);
        builder.Initialize();

        var totalWidth = paperWidth == PaperWidth.Width58mm ? 32 : 48;
        var separator = new string('-', totalWidth);

        builder.AlignCenter();
        builder.Bold(true).TextDoubleBoth().WriteLine("TEST RECEIPT").TextNormal().Bold(false);
        builder.WriteLine(store.StoreName);
        builder.WriteLine(separator);

        builder.AlignLeft();
        builder.WriteLine($"Terminal : {terminalCode}");
        builder.WriteLine($"Printer  : {printerName}");
        builder.WriteLine($"Width    : {paperWidth} ({totalWidth} col)");
        builder.WriteLine($"Encoding : {encoding} (CP:{codePage})");
        builder.WriteLine($"Date/Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.WriteLine(separator);

        builder.WriteLine("ALIGNMENT TEST:");
        builder.AlignLeft().WriteLine("<< Left Aligned >>");
        builder.AlignCenter().WriteLine("-- Center Aligned --");
        builder.AlignRight().WriteLine(">> Right Aligned <<");
        builder.AlignLeft();
        builder.WriteLine(separator);

        builder.WriteLine("STYLE TEST:");
        builder.Bold(true).WriteLine("Bold Text Enabled").Bold(false);
        builder.TextDoubleHeight().WriteLine("Double Height").TextNormal();
        builder.TextDoubleWidth().WriteLine("Double Width").TextNormal();
        builder.TextDoubleBoth().WriteLine("Double Both").TextNormal();
        builder.WriteLine(separator);

        builder.AlignCenter();
        builder.WriteLine("DIAGNOSTIC TEST PASSED");
        builder.WriteLine(separator);

        builder.FeedLines(3);
        if (openDrawer)
        {
            builder.KickDrawer(0);
        }
        builder.Cut(partial: true);

        return builder;
    }

    private static string FormatRow(string col1, string col2, string col3, string col4, int[] widths)
    {
        var c1 = col1.PadRight(widths[0])[..widths[0]];
        var c2 = col2.PadLeft(widths[1])[..widths[1]];
        var c3 = col3.PadLeft(widths[2])[..widths[2]];
        var c4 = col4.PadLeft(widths[3])[..widths[3]];
        return $"{c1}{c2}{c3}{c4}";
    }

    private static string FormatSummaryLine(string label, string value, int totalWidth)
    {
        var spaceCount = totalWidth - label.Length - value.Length;
        if (spaceCount < 1)
            spaceCount = 1;
        return label + new string(' ', spaceCount) + value;
    }

    private static string FormatMoney(long paise)
    {
        var sign = paise < 0 ? "-" : "";
        var abs = Math.Abs(paise);
        var rupees = abs / 100;
        var cents = abs % 100;
        return $"{sign}Rs.{rupees}.{cents:D2}";
    }

    private static string FormatDateTime(DateTime utcTime, string? timeZoneId)
    {
        var tzId = string.IsNullOrWhiteSpace(timeZoneId) ? "India Standard Time" : timeZoneId.Trim();
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcTime, tz);
            return local.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return utcTime.ToString("dd/MM/yyyy HH:mm:ss UTC", CultureInfo.InvariantCulture);
        }
    }

    private static List<string> WrapText(string text, int maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add("");
            return result;
        }

        var words = text.Split(' ');
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            if (currentLine.Length == 0)
            {
                if (word.Length > maxWidth)
                {
                    result.Add(word[..maxWidth]);
                }
                else
                {
                    currentLine.Append(word);
                }
            }
            else if (currentLine.Length + 1 + word.Length <= maxWidth)
            {
                currentLine.Append(' ').Append(word);
            }
            else
            {
                result.Add(currentLine.ToString());
                currentLine.Clear();
                currentLine.Append(word.Length > maxWidth ? word[..maxWidth] : word);
            }
        }

        if (currentLine.Length > 0)
        {
            result.Add(currentLine.ToString());
        }

        return result.Count > 0 ? result : [text[..Math.Min(text.Length, maxWidth)]];
    }
}
