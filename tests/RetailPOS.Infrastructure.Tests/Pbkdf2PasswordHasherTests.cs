using RetailPOS.Domain.Common;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void HashPassword_GeneratesUniqueSaltsAndDistinctHashes()
    {
        var password = "SecureStorePassword@2026";

        var hash1 = _hasher.HashPassword(password);
        var hash2 = _hasher.HashPassword(password);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1, hash2); // Salts must be cryptographically random and distinct
        Assert.StartsWith("$pbkdf2-sha256$v=1$i=600000$", hash1, StringComparison.Ordinal);
        Assert.StartsWith("$pbkdf2-sha256$v=1$i=600000$", hash2, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPassword_WithCorrectPassword_ReturnsTrue()
    {
        var password = "Correct#Password!123";
        var hash = _hasher.HashPassword(password);

        var isValid = _hasher.VerifyPassword(password, hash);

        Assert.True(isValid);
    }

    [Fact]
    public void VerifyPassword_WithIncorrectPassword_ReturnsFalse()
    {
        var password = "Correct#Password!123";
        var hash = _hasher.HashPassword(password);

        var isValid = _hasher.VerifyPassword("WrongPassword!123", hash);

        Assert.False(isValid);
    }

    [Fact]
    public void PasswordPolicy_DoesNotSilentlyTrimWhitespace()
    {
        var passwordWithSpaces = "  passwordWithPadding123  ";
        var hash = _hasher.HashPassword(passwordWithSpaces);

        // Exact preservation: verifying without spaces must fail!
        Assert.False(_hasher.VerifyPassword("passwordWithPadding123", hash));
        Assert.True(_hasher.VerifyPassword(passwordWithSpaces, hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")] // 5 chars
    [InlineData("1234567")] // 7 chars
    public void PasswordPolicy_RejectsInvalidPasswords(string invalidPassword)
    {
        Assert.Throws<DomainException>(() => _hasher.HashPassword(invalidPassword));
    }

    [Fact]
    public void PasswordPolicy_RejectsPasswordsLongerThan128Characters()
    {
        var tooLong = new string('A', 129);
        var ex = Assert.Throws<DomainException>(() => _hasher.HashPassword(tooLong));
        Assert.Equal("PASSWORD_TOO_LONG", ex.ErrorCode);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("9999")]
    [InlineData("123456")]
    public void HashPin_ValidPins_HashesAndVerifies(string pin)
    {
        var hash = _hasher.HashPin(pin);

        Assert.NotNull(hash);
        Assert.True(_hasher.VerifyPin(pin, hash));
        Assert.False(_hasher.VerifyPin("0000", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")] // too short (3 digits)
    [InlineData("1234567")] // too long (7 digits)
    [InlineData("abcd")] // non-numeric
    [InlineData("12a4")] // alphanumeric
    public void PinPolicy_RejectsInvalidPins(string invalidPin)
    {
        Assert.Throws<DomainException>(() => _hasher.HashPin(invalidPin));
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("$pbkdf2-sha256$v=1$invalid")]
    [InlineData("$pbkdf2-sha256$v=1$i=abc$s=bad$h=bad")]
    [InlineData("")]
    public void VerifyPassword_WithMalformedHash_SafelyReturnsFalse(string malformedHash)
    {
        var result = _hasher.VerifyPassword("ValidPassword123", malformedHash);
        Assert.False(result);
    }
}
