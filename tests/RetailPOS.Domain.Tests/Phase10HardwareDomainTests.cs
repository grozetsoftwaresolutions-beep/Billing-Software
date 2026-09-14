using System.Text;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Hardware;
using Xunit;

namespace RetailPOS.Domain.Tests;

public sealed class Phase10HardwareDomainTests
{
    private static readonly string[] LineSeparators = ["\r\n", "\r", "\n"];

    [Fact]
    public void EscPosBuilder_EmitsExactBinaryControlSequences()
    {
        var builder = new EscPosBuilder();
        builder.Initialize()
               .AlignCenter()
               .Bold(true)
               .TextDoubleBoth()
               .WriteLine("GROZET SUPERMARKET")
               .TextNormal()
               .Bold(false)
               .AlignLeft()
               .FeedLines(2)
               .Cut(partial: true)
               .KickDrawer(0);

        var bytes = builder.ToBytes();
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        // Verify ESC @ (1B 40)
        Assert.Equal(0x1B, bytes[0]);
        Assert.Equal(0x40, bytes[1]);

        // Verify ESC a 1 (1B 61 01) Align Center
        Assert.Equal(0x1B, bytes[2]);
        Assert.Equal(0x61, bytes[3]);
        Assert.Equal(0x01, bytes[4]);

        // Verify ESC E 1 (1B 45 01) Bold On
        Assert.Equal(0x1B, bytes[5]);
        Assert.Equal(0x45, bytes[6]);
        Assert.Equal(0x01, bytes[7]);

        // Verify ASCII preview contains header
        var preview = builder.ToAsciiPreview();
        Assert.Contains("GROZET SUPERMARKET", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptLayout80mm_RendersExact48ColumnLayoutWithDiscountsAndGst()
    {
        var store = new ReceiptStoreInfo(
            StoreName: "Grozet Fresh Supermarket",
            Address: "123 Main Road, Chennai, TN",
            Phone: "044-23456789",
            TaxRegistrationNumber: "33AAAAA0000A1Z5",
            FooterMessage: "Thank you! Visit again!");

        var invoice = new ReceiptInvoiceInfo(
            InvoiceNumber: "INV-2026-0001",
            CreatedAtUtc: new DateTime(2026, 9, 14, 10, 30, 0, DateTimeKind.Utc),
            CashierName: "Ramesh",
            TerminalCode: "POS-T1",
            CustomerName: "Suresh Kumar",
            CustomerPhone: "9876543210",
            CustomerOutstandingBalancePaise: 50000);

        var items = new List<ReceiptLineItem>
        {
            new("Basmati Rice 5kg Premium Extra Long Grain", 2.000m, "BAG", 45000, 5000, 85000),
            new("Toor Dal 1kg", 1.000m, "KG", 16000, 0, 16000)
        };

        var taxes = new List<ReceiptTaxLine>
        {
            new("CGST", 2.5m, 2525),
            new("SGST", 2.5m, 2525)
        };

        var payments = new List<ReceiptPaymentLine>
        {
            new(PaymentMethod.Cash, 101000, 110000, 9000)
        };

        var summary = new ReceiptFinancialSummary(
            GrossSubtotalPaise: 106000,
            LineDiscountsPaise: 5000,
            InvoiceDiscountPaise: 0,
            TaxableSubtotalPaise: 101000,
            TotalTaxPaise: 5050,
            RoundingPaise: 0,
            FinalPayablePaise: 101000,
            Items: items,
            Taxes: taxes,
            Payments: payments);

        var options = new ReceiptRenderOptions(
            PaperWidth: PaperWidth.Width80mm,
            Encoding: PrinterEncoding.Ascii,
            CodePage: 0,
            AutoCut: true,
            OpenDrawer: true,
            IsReprint: false,
            ReprintCount: 0);

        var builder = ReceiptLayoutEngine.BuildReceipt(store, invoice, summary, options);
        var preview = builder.ToAsciiPreview();

        // 80mm layout assertions
        Assert.Contains("Grozet Fresh Supermarket", preview, StringComparison.Ordinal);
        Assert.Contains("INV-2026-0001", preview, StringComparison.Ordinal);
        Assert.Contains("GSTIN: 33AAAAA0000A1Z5", preview, StringComparison.Ordinal);
        Assert.Contains("Disc: -Rs.50.00", preview, StringComparison.Ordinal);
        Assert.Contains("CGST (2.5%)", preview, StringComparison.Ordinal);
        Assert.Contains("SGST (2.5%)", preview, StringComparison.Ordinal);
        Assert.Contains("TOTAL PAYABLE", preview, StringComparison.Ordinal);
        Assert.Contains("Cash", preview, StringComparison.Ordinal);
        Assert.Contains("Tendered", preview, StringComparison.Ordinal);
        Assert.Contains("Change Given", preview, StringComparison.Ordinal);

        // Verify line lengths do not exceed 48 characters
        var lines = preview.Split(LineSeparators, StringSplitOptions.None);
        foreach (var line in lines)
        {
            Assert.True(line.Length <= 48, $"Line exceeded 48 characters: '{line}' ({line.Length})");
        }
    }

    [Fact]
    public void ReceiptLayout58mm_RendersExact32ColumnLayout()
    {
        var store = new ReceiptStoreInfo(
            StoreName: "Mini Supermarket",
            Address: "Salem",
            Phone: "9876500000",
            TaxRegistrationNumber: null,
            FooterMessage: "Thank you!");

        var invoice = new ReceiptInvoiceInfo(
            InvoiceNumber: "INV-58-001",
            CreatedAtUtc: DateTime.UtcNow,
            CashierName: "Kavitha",
            TerminalCode: "T2");

        var items = new List<ReceiptLineItem>
        {
            new("Milk 500ml", 1.000m, "PKT", 3000, 0, 3000)
        };

        var summary = new ReceiptFinancialSummary(
            GrossSubtotalPaise: 3000,
            LineDiscountsPaise: 0,
            InvoiceDiscountPaise: 0,
            TaxableSubtotalPaise: 3000,
            TotalTaxPaise: 0,
            RoundingPaise: 0,
            FinalPayablePaise: 3000,
            Items: items,
            Taxes: new List<ReceiptTaxLine>(),
            Payments: new List<ReceiptPaymentLine> { new(PaymentMethod.Cash, 3000, 3000, 0) });

        var options = new ReceiptRenderOptions(
            PaperWidth: PaperWidth.Width58mm,
            Encoding: PrinterEncoding.Ascii,
            CodePage: 0,
            AutoCut: true,
            OpenDrawer: false,
            IsReprint: false,
            ReprintCount: 0);

        var builder = ReceiptLayoutEngine.BuildReceipt(store, invoice, summary, options);
        var preview = builder.ToAsciiPreview();

        // 58mm layout assertions: all lines <= 32 characters
        var lines = preview.Split(LineSeparators, StringSplitOptions.None);
        foreach (var line in lines)
        {
            Assert.True(line.Length <= 32, $"58mm line exceeded 32 characters: '{line}' ({line.Length})");
        }
    }

    [Fact]
    public void ReceiptReprintLayout_DisplaysReprintWatermarkAndCounter()
    {
        var store = new ReceiptStoreInfo(
            StoreName: "Grozet Mart",
            Address: "Chennai",
            Phone: "044-11112222",
            TaxRegistrationNumber: "33AAAAA1111A1Z1",
            FooterMessage: null);

        var invoice = new ReceiptInvoiceInfo(
            InvoiceNumber: "INV-2026-9999",
            CreatedAtUtc: DateTime.UtcNow,
            CashierName: "Admin",
            TerminalCode: "T1");

        var summary = new ReceiptFinancialSummary(
            5000, 0, 0, 5000, 0, 0, 5000,
            new List<ReceiptLineItem> { new("Bread", 1m, "PCS", 5000, 0, 5000) },
            new List<ReceiptTaxLine>(),
            new List<ReceiptPaymentLine> { new(PaymentMethod.Cash, 5000, 5000, 0) });

        var options = new ReceiptRenderOptions(
            PaperWidth: PaperWidth.Width80mm,
            Encoding: PrinterEncoding.Ascii,
            CodePage: 0,
            AutoCut: true,
            OpenDrawer: false,
            IsReprint: true,
            ReprintCount: 2);

        var builder = ReceiptLayoutEngine.BuildReceipt(store, invoice, summary, options);
        var preview = builder.ToAsciiPreview();

        Assert.Contains("*** DUPLICATE / REPRINT ***", preview, StringComparison.Ordinal);
        Assert.Contains("Reprint #2", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptEncodingStrategy_TransliteratesRupeeAndTamilTextCleanly()
    {
        // 1. Rupee symbol
        var textWithRupee = "Total: \u20B9 500.00";
        var normalized = ReceiptEncodingStrategy.NormalizeForPrinter(textWithRupee, PrinterEncoding.Ascii);
        Assert.Contains("Rs.", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\u20B9", normalized, StringComparison.Ordinal);

        // 2. Tamil text fallback transliteration: \u0BAA\u0BB0\u0BC1\u0BAA\u0BCD\u0BAA\u0BC1 (Paruppu) \u0BA4\u0BCB\u0B9A\u0BC8 (Dosai)
        var tamilText = "\u0BAA\u0BB0\u0BC1\u0BAA\u0BCD\u0BAA\u0BC1 \u0BA4\u0BCB\u0B9A\u0BC8";
        var transliterated = ReceiptEncodingStrategy.TransliterateIndicToRoman(tamilText);
        Assert.Contains("Paruppu", transliterated, StringComparison.Ordinal);
        Assert.Contains("Dosai", transliterated, StringComparison.Ordinal);

        // 3. Encode to safe bytes
        var bytes = ReceiptEncodingStrategy.Encode("\u0BAA\u0BB0\u0BC1\u0BAA\u0BCD\u0BAA\u0BC1 - \u20B9 50", PrinterEncoding.Ascii, 0);
        Assert.NotNull(bytes);
        var backToAscii = Encoding.ASCII.GetString(bytes);
        Assert.Contains("Paruppu", backToAscii, StringComparison.Ordinal);
        Assert.Contains("Rs.", backToAscii, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptEncodingStrategy_SupportsCustomCodePages_CP437_CP850_AndFallback()
    {
        // 1. CP437 encoding
        var textCp437 = "Box: ÉÍ» Total: 50.00";
        var bytesCp437 = ReceiptEncodingStrategy.Encode(textCp437, PrinterEncoding.Custom, 437);
        Assert.NotNull(bytesCp437);
        Assert.NotEmpty(bytesCp437);

        // 2. CP850 encoding
        var textCp850 = "Café Crème - 5.00";
        var bytesCp850 = ReceiptEncodingStrategy.Encode(textCp850, PrinterEncoding.Custom, 850);
        Assert.NotNull(bytesCp850);
        Assert.NotEmpty(bytesCp850);

        // 3. Mixed English + Tamil
        var mixedText = "Ponni Arisi 5kg - பொன்னி அரிசி - ₹ 250";
        var mixedBytes = ReceiptEncodingStrategy.Encode(mixedText, PrinterEncoding.Ascii, 0);
        var mixedAscii = System.Text.Encoding.ASCII.GetString(mixedBytes);
        Assert.Contains("Ponni Arisi", mixedAscii);
        Assert.Contains("Rs. 250", mixedAscii);

        // 4. Devanagari (Hindi) fallback/clean substitution without crashing
        var hindiText = "चावल (Rice) - ₹ 100";
        var hindiBytes = ReceiptEncodingStrategy.Encode(hindiText, PrinterEncoding.Ascii, 0);
        Assert.NotNull(hindiBytes);
        var hindiAscii = System.Text.Encoding.ASCII.GetString(hindiBytes);
        Assert.Contains("(Rice) - Rs. 100", hindiAscii);

        // 5. Unsupported emoji character replaced with '?' without crashing
        var emojiText = "Special Deal 😀";
        var emojiBytes = ReceiptEncodingStrategy.Encode(emojiText, PrinterEncoding.Ascii, 0);
        var emojiAscii = System.Text.Encoding.ASCII.GetString(emojiBytes);
        Assert.Contains("Special Deal ?", emojiAscii);
    }
}
