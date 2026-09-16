using System.Text.RegularExpressions;

namespace RetailPOS.Application.Security;

public static class AuditJsonSanitizer
{
    private static readonly Regex ProhibitedKeyPattern = new(
        @"""(password|pin|hash|salt|token|key|secret|auth|card|cvv|recovery)""\s*:\s*""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Sanitize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        return ProhibitedKeyPattern.Replace(json, "\"$1\": \"[REDACTED]\"");
    }
}
