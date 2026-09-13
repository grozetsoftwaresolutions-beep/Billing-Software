using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RetailPOS.Application.Security;
using RetailPOS.Domain.Common;

namespace RetailPOS.Infrastructure.Security;

/// <summary>
/// Production PBKDF2-HMAC-SHA256 password and PIN hasher.
/// - Iterations: 600,000
/// - Salt: 32 bytes (cryptographically secure random)
/// - Subkey: 32 bytes
/// - Comparison: CryptographicOperations.FixedTimeEquals
/// - Format: $pbkdf2-sha256$v=1$i=600000$s=<salt>$h=<hash>
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string AlgorithmName = "pbkdf2-sha256";
    private const int CurrentVersion = 1;
    private const int DefaultIterations = 600_000;
    private const int SaltSizeBytes = 32;
    private const int SubkeySizeBytes = 32;

    private static readonly Regex PinRegex = new(@"^\d{4,6}$", RegexOptions.Compiled);

    public string HashPassword(string password)
    {
        ValidatePasswordPolicy(password);
        return HashInternal(password);
    }

    public bool VerifyPassword(string password, string hashedPassword)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(hashedPassword))
        {
            return false;
        }

        return VerifyInternal(password, hashedPassword);
    }

    public string HashPin(string pin)
    {
        ValidatePinPolicy(pin);
        return HashInternal(pin);
    }

    public bool VerifyPin(string pin, string hashedPin)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrWhiteSpace(hashedPin))
        {
            return false;
        }

        if (!PinRegex.IsMatch(pin))
        {
            return false;
        }

        return VerifyInternal(pin, hashedPin);
    }

    public void ValidatePasswordPolicy(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new DomainException("Password cannot be empty or whitespace-only.", "INVALID_PASSWORD");
        }

        if (password.Length < 8)
        {
            throw new DomainException("Password must be at least 8 characters long.", "PASSWORD_TOO_SHORT");
        }

        if (password.Length > 128)
        {
            throw new DomainException("Password cannot exceed 128 characters.", "PASSWORD_TOO_LONG");
        }
    }

    public void ValidatePinPolicy(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin) || !PinRegex.IsMatch(pin))
        {
            throw new DomainException("PIN must be between 4 and 6 numeric digits.", "INVALID_PIN");
        }
    }

    private static string HashInternal(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            secretBytes,
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA256,
            SubkeySizeBytes);

        var saltBase64 = Convert.ToBase64String(salt);
        var keyBase64 = Convert.ToBase64String(derivedKey);

        return string.Format(
            CultureInfo.InvariantCulture,
            "${0}$v={1}$i={2}$s={3}$h={4}",
            AlgorithmName,
            CurrentVersion,
            DefaultIterations,
            saltBase64,
            keyBase64);
    }

    private static bool VerifyInternal(string secret, string formattedHash)
    {
        try
        {
            // Expected format: $pbkdf2-sha256$v=1$i=600000$s=<salt>$h=<hash>
            var parts = formattedHash.Split('$');
            if (parts.Length != 6)
            {
                return false;
            }

            if (!string.Equals(parts[1], AlgorithmName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!parts[2].StartsWith("v=", StringComparison.Ordinal) ||
                !parts[3].StartsWith("i=", StringComparison.Ordinal) ||
                !parts[4].StartsWith("s=", StringComparison.Ordinal) ||
                !parts[5].StartsWith("h=", StringComparison.Ordinal))
            {
                return false;
            }

            if (!int.TryParse(parts[3][2..], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations <= 0)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[4][2..]);
            var expectedKey = Convert.FromBase64String(parts[5][2..]);

            if (salt.Length != SaltSizeBytes || expectedKey.Length != SubkeySizeBytes)
            {
                return false;
            }

            var secretBytes = Encoding.UTF8.GetBytes(secret);
            var computedKey = Rfc2898DeriveBytes.Pbkdf2(
                secretBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedKey.Length);

            return CryptographicOperations.FixedTimeEquals(computedKey, expectedKey);
        }
        catch
        {
            // Any format or decoding failure safely returns false without crashing
            return false;
        }
    }
}
