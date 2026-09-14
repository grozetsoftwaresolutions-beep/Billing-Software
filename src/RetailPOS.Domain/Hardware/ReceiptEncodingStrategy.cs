using System.Text;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Hardware;

public static class ReceiptEncodingStrategy
{
    static ReceiptEncodingStrategy()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly Dictionary<string, string> TamilTransliterationMap = new(StringComparer.Ordinal)
    {
        { "\u0BAA\u0BB0\u0BC1\u0BAA\u0BCD\u0BAA\u0BC1", "Paruppu" },              // பருப்பு
        { "\u0BA4\u0BCB\u0B9A\u0BC8", "Dosai" },                                  // தோசை
        { "\u0B85\u0BB0\u0BBF\u0B9A\u0BBF", "Arisi" },                            // அரிசி
        { "\u0BAA\u0BCA\u0BA9\u0BCD\u0BA9\u0BBF \u0B85\u0BB0\u0BBF\u0B9A\u0BBF", "Ponni Arisi" }, // பொன்னி அரிசி
        { "\u0BAA\u0BCA\u0BA9\u0BCD\u0BA9\u0BBF", "Ponni" },                      // பொன்னி
        { "\u0B9A\u0B95\u0BCD\u0B95\u0BB0\u0BC8", "Sarkarai" },                    // சக்கரை
        { "\u0BAA\u0BBE\u0BB2\u0BCD", "Paal" },                                    // பால்
        { "\u0B8E\u0BA3\u0BCD\u0BA3\u0BC6\u0BA9\u0BCD", "Ennai" },                // எண்ணெய்
        { "\u0BA4\u0BC7\u0BA8\u0BC0\u0BB0\u0BCD", "Theneer" },                    // தேநீர்
        { "\u0B95\u0BBE\u0BAA\u0BBF", "Coffee" },                                 // காபி
        { "\u0B95\u0BCB\u0BA4\u0BC1\u0BAE\u0BC8", "Godhumai" },                   // கோதுமை
        { "\u0B89\u0BA4\u0BCD\u0BA4\u0BC1", "Uppu" },                             // உப்பு
        { "\u0BAE\u0BBF\u0BB3\u0B95\u0BBE\u0BAF\u0BCD", "Milagai" },              // மிளகாய்
        { "\u0BB5\u0BC6\u0B99\u0BCD\u0B95\u0BBE\u0BAF\u0BAE\u0BCD", "Vengayam" }, // வெங்காயம்
        { "\u0BA4\u0B95\u0BCD\u0B95\u0BBE\u0BB3\u0BBF", "Thakkali" }              // தக்காளி
    };

    public static string TransliterateIndicToRoman(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var processed = text;
        foreach (var kvp in TamilTransliterationMap)
        {
            if (processed.Contains(kvp.Key, StringComparison.Ordinal))
            {
                processed = processed.Replace(kvp.Key, kvp.Value, StringComparison.Ordinal);
            }
        }
        return processed;
    }

    public static string NormalizeForPrinter(string text, PrinterEncoding encoding, int codePage = 0)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // 1. Replace Indian Rupee symbol (U+20B9) with standard "Rs."
        var processed = text.Replace("\u20B9", "Rs.", StringComparison.Ordinal);

        // 2. Transliterate Indic/Tamil scripts
        processed = TransliterateIndicToRoman(processed);

        // 3. Fallback for non-ASCII characters if not Utf8
        if (encoding == PrinterEncoding.Utf8)
            return processed;

        var sb = new StringBuilder(processed.Length);
        foreach (var c in processed)
        {
            if (c <= 127)
            {
                sb.Append(c);
            }
            else if (encoding == PrinterEncoding.Windows1252 && c >= 160 && c <= 255)
            {
                sb.Append(c);
            }
            else if (encoding == PrinterEncoding.Custom)
            {
                // In custom code page mode, allow characters that the target encoding supports, else substitute '?'
                sb.Append(c);
            }
            else
            {
                // Substitute clean placeholder for unsupported non-Latin glyphs
                sb.Append('?');
            }
        }

        return sb.ToString();
    }

    public static byte[] Encode(string text, PrinterEncoding encoding, int codePage = 0)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        if (encoding == PrinterEncoding.Utf8)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        var normalized = NormalizeForPrinter(text, encoding, codePage);

        if (encoding == PrinterEncoding.Custom && codePage > 0)
        {
            try
            {
                var customEnc = Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
                return customEnc.GetBytes(normalized);
            }
            catch
            {
                // Fallback to ASCII if specified code page is not found on machine
            }
        }

        var enc = encoding == PrinterEncoding.Windows1252
            ? Encoding.GetEncoding("ISO-8859-1")
            : Encoding.ASCII;

        return enc.GetBytes(normalized);
    }

    public static string TransliterateForPreview(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var processed = text.Replace("\u20B9", "Rs.", StringComparison.Ordinal);
        return TransliterateIndicToRoman(processed);
    }
}
