namespace RetailPOS.Application.Security;

/// <summary>
/// Cryptographic hasher contract for passwords and POS PINs.
/// Enforces PBKDF2-HMAC-SHA256 with 600,000 iterations and constant-time verification.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes a password using PBKDF2-HMAC-SHA256 with 600,000 iterations and 32-byte random salt.
    /// Input password is preserved exactly as supplied (never trimmed).
    /// </summary>
    string HashPassword(string password);

    /// <summary>
    /// Verifies a password against a versioned PBKDF2 hash using constant-time comparison.
    /// </summary>
    bool VerifyPassword(string password, string hashedPassword);

    /// <summary>
    /// Validates and hashes a 4-to-6 digit numeric PIN using PBKDF2.
    /// </summary>
    string HashPin(string pin);

    /// <summary>
    /// Verifies a PIN against a versioned PBKDF2 hash using constant-time comparison.
    /// </summary>
    bool VerifyPin(string pin, string hashedPin);

    /// <summary>
    /// Validates password policy: 8 to 128 characters, not empty/whitespace-only.
    /// </summary>
    void ValidatePasswordPolicy(string password);

    /// <summary>
    /// Validates PIN policy: 4 to 6 numeric digits.
    /// </summary>
    void ValidatePinPolicy(string pin);
}
