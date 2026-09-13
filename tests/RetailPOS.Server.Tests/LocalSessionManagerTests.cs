using RetailPOS.Domain.Enums;
using RetailPOS.Server.Authentication;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class LocalSessionManagerTests
{
    [Fact]
    public async Task CreateSession_GeneratesValidToken_AndValidatesSuccessfully()
    {
        var manager = new LocalSessionManager();
        var userId = Guid.NewGuid();
        var username = "cashier1";
        var role = RoleType.Cashier;

        var token = manager.CreateSession(userId, username, role, TimeSpan.FromHours(8));

        Assert.NotNull(token);
        Assert.Equal(64, token.Length); // 32 bytes hex encoded = 64 characters

        var validationResult = await manager.ValidateSessionAsync(token);

        Assert.True(validationResult.IsValid);
        Assert.Equal(userId, validationResult.UserId);
        Assert.Equal(username, validationResult.Username);
        Assert.Equal(role, validationResult.Role);
        Assert.Null(validationResult.ErrorMessage);
    }

    [Fact]
    public async Task ValidateSession_WithEmptyOrUnknownToken_ReturnsFailure()
    {
        var manager = new LocalSessionManager();

        var emptyResult = await manager.ValidateSessionAsync("");
        Assert.False(emptyResult.IsValid);
        Assert.Equal("Missing session token.", emptyResult.ErrorMessage);

        var unknownResult = await manager.ValidateSessionAsync("unknown-token-1234567890abcdef");
        Assert.False(unknownResult.IsValid);
        Assert.Equal("Invalid session token.", unknownResult.ErrorMessage);
    }

    [Fact]
    public async Task ValidateSession_WithExpiredToken_FailsAndRemovesSession()
    {
        var manager = new LocalSessionManager();
        var userId = Guid.NewGuid();

        // Create session already expired
        var token = manager.CreateSession(userId, "supervisor", RoleType.Manager, TimeSpan.FromMilliseconds(-100));

        var expiredResult = await manager.ValidateSessionAsync(token);

        Assert.False(expiredResult.IsValid);
        Assert.Equal("Session has expired.", expiredResult.ErrorMessage);

        // Next validation should confirm it was purged
        var purgedResult = await manager.ValidateSessionAsync(token);
        Assert.False(purgedResult.IsValid);
        Assert.Equal("Invalid session token.", purgedResult.ErrorMessage);
    }

    [Fact]
    public async Task RevokeSession_ImmediatelyInvalidatesToken()
    {
        var manager = new LocalSessionManager();
        var userId = Guid.NewGuid();

        var token = manager.CreateSession(userId, "admin", RoleType.Admin, TimeSpan.FromHours(1));

        var validResult = await manager.ValidateSessionAsync(token);
        Assert.True(validResult.IsValid);

        manager.RevokeSession(token);

        var revokedResult = await manager.ValidateSessionAsync(token);
        Assert.False(revokedResult.IsValid);
        Assert.Equal("Invalid session token.", revokedResult.ErrorMessage);
    }

    [Fact]
    public async Task LocalSessionManager_ThreadSafety_HandlesConcurrentSessions()
    {
        var manager = new LocalSessionManager();
        var tasks = new List<Task>();

        for (int i = 0; i < 50; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                var userId = Guid.NewGuid();
                var token = manager.CreateSession(userId, $"user_{idx}", RoleType.Cashier, TimeSpan.FromMinutes(10));
                var result = await manager.ValidateSessionAsync(token);
                Assert.True(result.IsValid);
                Assert.Equal(userId, result.UserId);
            }));
        }

        await Task.WhenAll(tasks);
    }
}
