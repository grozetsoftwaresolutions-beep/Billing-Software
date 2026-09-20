namespace RetailPOS.Infrastructure.Diagnostics;

using System;
using System.Text.RegularExpressions;

public sealed class DiagnosticSecretScrubber
{
    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled);

    private static readonly Regex BearerHeaderRegex = new(
        @"Bearer\s+[A-Za-z0-9\._\-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RecoveryKeyRegex = new(
        @"\b[0-9a-fA-F]{64}\b",
        RegexOptions.Compiled);

    private static readonly Regex PasswordFieldRegex = new(
        @"(?i)(""(?:password|pwd|client_secret|secret|installationSecret|token|refreshToken|apiKey)""\s*:\s*"")([^""]+)("")",
        RegexOptions.Compiled);

    private static readonly Regex PlainKeyValuePasswordRegex = new(
        @"(?i)\b(password|pwd|client_secret|installationSecret|secret)\s*[:=]\s*(?!\[REDACTED)([^\s,;]+)",
        RegexOptions.Compiled);

    private static readonly Regex PasswordUrlParamRegex = new(
        @"(?i)(password|pwd|token|secret|apikey)=(?!\[REDACTED)([^&\s;]+)",
        RegexOptions.Compiled);

    private static readonly Regex PinRegex = new(
        @"(?i)(""(?:pin|staffPin|userPin|supervisorPin)""\s*:\s*"")([^""]+)("")",
        RegexOptions.Compiled);

    private static readonly Regex PrivateKeyBlockRegex = new(
        @"-----BEGIN [A-Z ]+PRIVATE KEY-----[\s\S]*?-----END [A-Z ]+PRIVATE KEY-----",
        RegexOptions.Compiled);

    private static readonly Regex ConnectionStringPasswordRegex = new(
        @"(?i)(Password|Pwd)\s*=\s*(?!\[REDACTED)[^;]+",
        RegexOptions.Compiled);

    private static readonly Regex PotentialCardRegex = new(
        @"\b(?:\d[ -]*?){13,19}\b",
        RegexOptions.Compiled);

    public static string Scrub(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = input;

        // 1. Private keys
        result = PrivateKeyBlockRegex.Replace(result, "[REDACTED_AUTH_SECRET]");

        // 2. JWTs and Bearer tokens
        result = JwtRegex.Replace(result, "[REDACTED_JWT]");
        result = BearerHeaderRegex.Replace(result, "Bearer [REDACTED_JWT]");

        // 3. DB connection string credentials
        result = ConnectionStringPasswordRegex.Replace(result, "$1=[REDACTED_CONNECTION_STRING]");

        // 4. Password JSON and plain text key-value fields
        result = PasswordFieldRegex.Replace(result, "$1[REDACTED_PASSWORD]$3");
        result = PlainKeyValuePasswordRegex.Replace(result, "$1: [REDACTED_PASSWORD]");

        // 5. URL query password params
        result = PasswordUrlParamRegex.Replace(result, "$1=[REDACTED_SECRET]");

        // 6. PIN JSON fields
        result = PinRegex.Replace(result, "$1[REDACTED_PIN]$3");

        // 7. Store Master Recovery Key / Raw 64-hex keys
        result = RecoveryKeyRegex.Replace(result, "[REDACTED_RECOVERY_KEY]");

        // 8. Cardholder PANs matching Luhn check
        result = PotentialCardRegex.Replace(result, match =>
        {
            var rawDigits = match.Value.Replace(" ", "").Replace("-", "");
            if (rawDigits.Length >= 13 && rawDigits.Length <= 19 && IsLuhnValid(rawDigits))
            {
                return "[REDACTED_PAN]";
            }
            return match.Value;
        });

        return result;
    }

    private static bool IsLuhnValid(string digits)
    {
        int sum = 0;
        bool alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            char c = digits[i];
            if (c < '0' || c > '9') return false;
            int n = c - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return (sum % 10 == 0);
    }
}
