using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Domain.Tests;

public sealed class UserSecurityTests
{
    [Fact]
    public void RecordFailedPinAttempt_LocksOutAfterFiveAttempts()
    {
        var user = new User(Guid.NewGuid(), "cashier1", "Cashier One", "pinhash", "passhash", "salt", RoleType.Cashier);
        var baseTime = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        var lockoutDuration = TimeSpan.FromMinutes(15);

        Assert.Equal(0, user.FailedPinAttempts);
        Assert.False(user.IsLockedOut(baseTime));

        // 4 failed attempts: not locked out yet
        for (int i = 1; i <= 4; i++)
        {
            user.RecordFailedPinAttempt(5, lockoutDuration, baseTime);
            Assert.Equal(i, user.FailedPinAttempts);
            Assert.False(user.IsLockedOut(baseTime));
        }

        // 5th failed attempt: triggers lockout
        user.RecordFailedPinAttempt(5, lockoutDuration, baseTime);
        Assert.Equal(5, user.FailedPinAttempts);
        Assert.True(user.IsLockedOut(baseTime));

        // Still locked out 10 minutes later
        Assert.True(user.IsLockedOut(baseTime.AddMinutes(10)));

        // Lockout expired 16 minutes later
        Assert.False(user.IsLockedOut(baseTime.AddMinutes(16)));
    }

    [Fact]
    public void ResetPinLockout_ClearsAttemptsAndLockoutTimestamp()
    {
        var user = new User(Guid.NewGuid(), "cashier2", "Cashier Two", "pinhash", "passhash", "salt", RoleType.Cashier);
        var now = DateTime.UtcNow;

        for (int i = 0; i < 5; i++)
        {
            user.RecordFailedPinAttempt(5, TimeSpan.FromMinutes(15), now);
        }

        Assert.True(user.IsLockedOut(now));

        user.ResetPinLockout();

        Assert.Equal(0, user.FailedPinAttempts);
        Assert.False(user.IsLockedOut(now));
    }
}
