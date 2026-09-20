namespace RetailPOS.Infrastructure.Diagnostics;

using System;
using System.Text.RegularExpressions;

public sealed class DiagnosticSecretAuditor
{
    private static readonly Regex UnscrubbedJwtRegex = new(
        @"\beyJhbGciOi[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.Compiled);

    private static readonly Regex UnscrubbedRecoveryKeyRegex = new(
        @"\b[0-9a-fA-F]{64}\b",
        RegexOptions.Compiled);

    private static readonly Regex UnscrubbedPrivateKeyRegex = new(
        @"-----BEGIN (?:[A-Z ]+)?PRIVATE KEY-----",
        RegexOptions.Compiled);

    private static readonly Regex UnscrubbedPasswordJsonRegex = new(
        @"(?i)""(?:password|pwd|client_secret|installationSecret)""\s*:\s*""(?!\[REDACTED)[^""]+""",
        RegexOptions.Compiled);

    private static readonly Regex UnscrubbedPinJsonRegex = new(
        @"(?i)""(?:pin|staffPin|userPin|supervisorPin)""\s*:\s*""(?!\[REDACTED)[^""]+""",
        RegexOptions.Compiled);

    private static readonly Regex PotentialCardRegex = new(
        @"\b(?:\d[ -]*?){13,19}\b",
        RegexOptions.Compiled);

    public record AuditResult(bool IsClean, string? ViolationCategory, string? Details);

    public static AuditResult Audit(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new AuditResult(true, null, null);
        }

        if (UnscrubbedRecoveryKeyRegex.IsMatch(content))
        {
            return new AuditResult(false, "RECOVERY_OR_SIGNING_KEY", "A 64-hexadecimal key pattern was detected in the post-scrub payload.");
        }

        if (UnscrubbedJwtRegex.IsMatch(content))
        {
            return new AuditResult(false, "JWT_SESSION_TOKEN", "An unscrubbed JWT token header was detected in the post-scrub payload.");
        }

        if (UnscrubbedPrivateKeyRegex.IsMatch(content))
        {
            return new AuditResult(false, "PRIVATE_KEY_BLOCK", "A private key block was detected in the post-scrub payload.");
        }

        if (UnscrubbedPasswordJsonRegex.IsMatch(content))
        {
            return new AuditResult(false, "PASSWORD_SECRET", "An unscrubbed password or secret field was detected in the post-scrub payload.");
        }

        if (UnscrubbedPinJsonRegex.IsMatch(content))
        {
            return new AuditResult(false, "USER_PIN", "An unscrubbed PIN field was detected in the post-scrub payload.");
        }

        var cardMatches = PotentialCardRegex.Matches(content);
        foreach (Match m in cardMatches)
        {
            var rawDigits = m.Value.Replace(" ", "").Replace("-", "");
            if (rawDigits.Length >= 13 && rawDigits.Length <= 19 && IsLuhnValid(rawDigits))
            {
                return new AuditResult(false, "CARDHOLDER_PAN", "A valid cardholder Primary Account Number was detected in the post-scrub payload.");
            }
        }

        return new AuditResult(true, null, null);
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
