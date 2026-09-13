using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Entities.Users;

public sealed class User : BaseEntity<Guid>
{
    public string Username { get; private set; }
    public string FullName { get; private set; }
    public string PinHash { get; private set; }
    public string PasswordHash { get; private set; }
    public string Salt { get; private set; }
    public RoleType Role { get; private set; }
    public bool IsActive { get; private set; }

    public User(Guid id, string username, string fullName, string pinHash, string passwordHash, string salt, RoleType role)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new DomainException("Username cannot be empty.", "INVALID_USERNAME");
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Full name cannot be empty.", "INVALID_USER_NAME");

        Id = id;
        Username = username.Trim().ToLowerInvariant();
        FullName = fullName.Trim();
        PinHash = pinHash;
        PasswordHash = passwordHash;
        Salt = salt;
        Role = role;
        IsActive = true;
    }

    public void Deactivate() => IsActive = false;
}
