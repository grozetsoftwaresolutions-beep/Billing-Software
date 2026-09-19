using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Security;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Server.Middleware;

namespace RetailPOS.Server.Endpoints;

public static class SecurityEndpoints
{
    private static readonly SemaphoreSlim RotationLock = new(1, 1);

    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/security");

        group.MapPost("/rotate-recovery-key", async (
            HttpContext context,
            RetailDbContext dbContext,
            IStoreIdentityService storeIdentityService,
            IServerIdentityService serverIdentityService,
            IAuditLogRepository auditRepo,
            CancellationToken ct) =>
        {
            if (serverIdentityService.CurrentState != ServerState.Available)
            {
                return Results.Json(
                    ApiResponse.Fail<object>("Server must be in Available state to initiate recovery key rotation.", "SERVER_NOT_AVAILABLE"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!await RotationLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
            {
                return Results.Json(
                    ApiResponse.Fail<object>("A recovery key rotation operation is currently in progress.", "ROTATION_LOCK_BUSY"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            byte[]? keyBBytes = null;
            byte[]? tokenBytes = null;
            char[]? keyBChars = null;
            try
            {
                var store = await dbContext.Stores.FirstOrDefaultAsync(ct);
                if (store == null)
                {
                    return Results.NotFound(ApiResponse.Fail<object>("Store not configured.", "STORE_NOT_FOUND"));
                }

                if (store.RotationState != null)
                {
                    return Results.Json(
                        ApiResponse.Fail<object>("A recovery key rotation is already staged or in progress.", "ROTATION_ALREADY_IN_PROGRESS"),
                        statusCode: StatusCodes.Status409Conflict);
                }

                var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? "ConsoleOwner";
                var currentUsername = context.Items["CurrentUsername"]?.ToString() ?? "Owner";

                // Generate Key B and confirmation token
                keyBBytes = RandomNumberGenerator.GetBytes(32);
                string keyBHex = Convert.ToHexString(keyBBytes);
                keyBChars = keyBHex.ToCharArray();

                tokenBytes = RandomNumberGenerator.GetBytes(32);
                string rawToken = Convert.ToHexString(tokenBytes).ToLowerInvariant();
                string tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
                DateTime expiresAtUtc = DateTime.UtcNow.AddMinutes(15);

                byte[] rootSigningKey = storeIdentityService.GetStoreSigningKey();
                Guid storeGuid = store.StoreGuid != Guid.Empty ? store.StoreGuid : storeIdentityService.StoreId;

                var (validatorHashB, wrappedKeyB) = storeIdentityService.WrapStoreSigningKey(
                    storeGuid,
                    keyBChars,
                    rootSigningKey);

                // Stage identity-B on disk
                await storeIdentityService.StageRotatedIdentityAsync(
                    storeGuid,
                    rootSigningKey,
                    keyBChars,
                    ct);

                // Record staged state in SQLite
                store.StageRotation(
                    validatorHashB,
                    wrappedKeyB,
                    tokenHash,
                    expiresAtUtc,
                    currentUserId);

                var audit = new AuditLog(
                    id: 0,
                    action: "RECOVERY_KEY_ROTATION_STAGED",
                    entityName: "Store",
                    entityId: storeGuid.ToString(),
                    userId: Guid.TryParse(currentUserId, out var uid) ? uid : null,
                    terminalId: null,
                    details: $"Recovery key rotation staged for store {storeGuid}. Token expires at {expiresAtUtc:O}",
                    success: true,
                    userName: currentUsername);

                dbContext.AuditLogs.Add(audit);
                await dbContext.SaveChangesAsync(ct);

                return Results.Ok(ApiResponse.Ok(new RotateRecoveryKeyResponseDto(
                    keyBHex,
                    rawToken,
                    expiresAtUtc)));
            }
            finally
            {
                if (keyBBytes != null) CryptographicOperations.ZeroMemory(keyBBytes);
                if (tokenBytes != null) CryptographicOperations.ZeroMemory(tokenBytes);
                if (keyBChars != null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(keyBChars.AsSpan()));
                RotationLock.Release();
            }
        })
        .RequirePermission(Permission.ManageStoreSettings)
        .WithName("RotateRecoveryKey")
        .WithSummary("Initiates two-phase recovery key rotation.");

        group.MapPost("/confirm-recovery-key-rotation", async (
            ConfirmRecoveryKeyRotationRequestDto request,
            HttpContext context,
            RetailDbContext dbContext,
            IStoreIdentityService storeIdentityService,
            IDatabaseMaintenanceCoordinator maintenanceCoordinator,
            IMaintenanceScopeFactory scopeFactory,
            IAuditLogRepository auditRepo,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
            {
                return Results.BadRequest(ApiResponse.Fail<object>("Confirmation token is required.", "INVALID_TOKEN"));
            }

            var store = await dbContext.Stores.FirstOrDefaultAsync(ct);
            if (store == null)
            {
                return Results.NotFound(ApiResponse.Fail<object>("Store not configured.", "STORE_NOT_FOUND"));
            }

            if (store.RotationState != "ROTATION_STAGED")
            {
                return Results.BadRequest(ApiResponse.Fail<object>("No staged recovery key rotation exists to confirm.", "NO_STAGED_ROTATION"));
            }

            if (store.RotationTokenExpiresAtUtc.HasValue && DateTime.UtcNow > store.RotationTokenExpiresAtUtc.Value)
            {
                return Results.Json(
                    ApiResponse.Fail<object>("The confirmation token has expired. Please cancel and initiate a new rotation.", "ROTATION_TOKEN_EXPIRED"),
                    statusCode: StatusCodes.Status410Gone);
            }

            string inputHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.ConfirmationToken.Trim()))).ToLowerInvariant();
            byte[] inputHashBytes = System.Text.Encoding.UTF8.GetBytes(inputHash);
            byte[] storedHashBytes = System.Text.Encoding.UTF8.GetBytes(store.RotationTokenHash ?? string.Empty);

            if (!CryptographicOperations.FixedTimeEquals(inputHashBytes, storedHashBytes))
            {
                return Results.BadRequest(ApiResponse.Fail<object>("Invalid confirmation token.", "INVALID_ROTATION_TOKEN"));
            }

            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? "ConsoleOwner";
            var currentUsername = context.Items["CurrentUsername"]?.ToString() ?? "Owner";

            // Mark commit requested and consume token
            store.RequestRotationCommit();
            await dbContext.SaveChangesAsync(ct);

            // Live Quiescence Critical Section
            using (var maintSession = await maintenanceCoordinator.EnterMaintenanceAsync("Recovery key rotation commit", ct))
            {
                using (scopeFactory.CreateMaintenanceScope())
                {
                    storeIdentityService.PromoteStagedIdentity();
                    store.FinalizeRotation();

                    var audit = new AuditLog(
                        id: 0,
                        action: "RECOVERY_KEY_ROTATION_COMMITTED",
                        entityName: "Store",
                        entityId: store.StoreGuid.ToString(),
                        userId: Guid.TryParse(currentUserId, out var uid) ? uid : null,
                        terminalId: null,
                        details: $"Recovery key rotation finalized and committed for store {store.StoreGuid}",
                        success: true,
                        userName: currentUsername);

                    dbContext.AuditLogs.Add(audit);
                    await dbContext.SaveChangesAsync(ct);
                    await storeIdentityService.ReloadActiveIdentityAsync(ct);
                }
            }

            return Results.Ok(ApiResponse.Ok(new ConfirmRecoveryKeyRotationResponseDto(
                "Recovery key rotation successfully committed.",
                DateTime.UtcNow)));
        })
        .RequirePermission(Permission.ManageStoreSettings)
        .WithName("ConfirmRecoveryKeyRotation")
        .WithSummary("Confirms receipt of new recovery key and commits rotation under maintenance quiescence.");

        group.MapPost("/cancel-recovery-key-rotation", async (
            HttpContext context,
            RetailDbContext dbContext,
            IStoreIdentityService storeIdentityService,
            CancellationToken ct) =>
        {
            var store = await dbContext.Stores.FirstOrDefaultAsync(ct);
            if (store == null)
            {
                return Results.NotFound(ApiResponse.Fail<object>("Store not configured.", "STORE_NOT_FOUND"));
            }

            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? "ConsoleOwner";
            var currentUsername = context.Items["CurrentUsername"]?.ToString() ?? "Owner";

            // Crash-Safe Cancellation: SQLite commit first, then delete staged file
            store.CancelRotation();

            var audit = new AuditLog(
                id: 0,
                action: "RECOVERY_KEY_ROTATION_CANCELLED",
                entityName: "Store",
                entityId: store.StoreGuid.ToString(),
                userId: Guid.TryParse(currentUserId, out var uid) ? uid : null,
                terminalId: null,
                details: $"Recovery key rotation cancelled for store {store.StoreGuid}. Authoritative key remains unchanged.",
                success: true,
                userName: currentUsername);

            dbContext.AuditLogs.Add(audit);
            await dbContext.SaveChangesAsync(ct);

            storeIdentityService.CleanStagedIdentity();

            return Results.Ok(ApiResponse.Ok(new { Message = "Recovery key rotation cancelled. Existing recovery key remains authoritative." }));
        })
        .RequirePermission(Permission.ManageStoreSettings)
        .WithName("CancelRecoveryKeyRotation")
        .WithSummary("Cancels pending recovery key rotation safely.");

        group.MapGet("/rotation-status", async (
            RetailDbContext dbContext,
            CancellationToken ct) =>
        {
            var store = await dbContext.Stores.AsNoTracking().FirstOrDefaultAsync(ct);
            if (store == null)
            {
                return Results.NotFound(ApiResponse.Fail<object>("Store not configured.", "STORE_NOT_FOUND"));
            }

            return Results.Ok(ApiResponse.Ok(new RotationStatusResponseDto(
                store.RotationState,
                store.RotationState != null,
                store.RotationTokenExpiresAtUtc,
                store.RotationInitiatedByUserId)));
        })
        .RequirePermission(Permission.ManageStoreSettings)
        .WithName("GetRotationStatus")
        .WithSummary("Gets current recovery key rotation state.");

        return endpoints;
    }
}
