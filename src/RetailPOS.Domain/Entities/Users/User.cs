using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Entities.Users;

public sealed class User : BaseEntity<Guid>
{
    public string Username { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public string PinHash { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public string Salt { get; private set; } = null!;
    public RoleType Role { get; private set; }
    public bool IsActive { get; private set; }
    public int FailedPinAttempts { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }

    private User() { }

    public User(Guid id, string username, string fullName, string pinHash, string passwordHash, string salt, RoleType role)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new DomainException("Username cannot be empty.", "INVALID_USERNAME");
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Full name cannot be empty.", "INVALID_USER_NAME");

        Id = id;
        Username = username.Trim().ToLowerInvariant();
        FullName = fullName.Trim();
        PinHash = pinHash ?? string.Empty;
        PasswordHash = passwordHash ?? string.Empty;
        Salt = salt ?? string.Empty;
        Role = role;
        IsActive = true;
        FailedPinAttempts = 0;
        LockoutEndUtc = null;
    }

    public void UpdateCredentials(string passwordHash, string pinHash, string salt)
    {
        PasswordHash = passwordHash ?? string.Empty;
        PinHash = pinHash ?? string.Empty;
        Salt = salt ?? string.Empty;
        ResetPinLockout();
    }

    public void UpdateRole(RoleType role)
    {
        Role = role;
    }

    public void Activate() => IsActive = true;

    public void Deactivate()
    {
        IsActive = false;
        ResetPinLockout();
    }

    public bool IsLockedOut(DateTime utcNow)
    {
        return LockoutEndUtc.HasValue && LockoutEndUtc.Value > utcNow;
    }

    public void RecordFailedPinAttempt(int maxAttempts, TimeSpan lockoutDuration, DateTime utcNow)
    {
        FailedPinAttempts++;
        if (FailedPinAttempts >= maxAttempts)
        {
            LockoutEndUtc = utcNow.Add(lockoutDuration);
        }
    }

    public void ResetPinLockout()
    {
        FailedPinAttempts = 0;
        LockoutEndUtc = null;
    }
}
