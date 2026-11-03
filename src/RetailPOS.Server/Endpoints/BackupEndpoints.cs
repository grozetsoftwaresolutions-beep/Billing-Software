namespace RetailPOS.Server.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Security;
using RetailPOS.Server.Middleware;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/backups");

        // POST /api/v1/backups - Create manual backup (Owner/Admin only)
        group.MapPost("", async (
            CreateBackupRequestDto? request,
            IBackupService backupService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = httpContext.Items["CurrentUserId"]?.ToString()
                ?? throw new InvalidOperationException("CurrentUserId is missing from authenticated context.");
            var userName = (string?)httpContext.Items["CurrentUsername"]
                ?? throw new InvalidOperationException("CurrentUsername is missing from authenticated context.");

            try
            {
                var record = await backupService.CreateBackupAsync(BackupType.Manual, userId, userName, request?.Reason, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/backups/{record.BackupOperationId}", ApiResponse.Ok(record, "Backup created successfully."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "OPERATION_ALREADY_IN_PROGRESS")
            {
                return Results.Conflict(ApiResponse.Fail<BackupRecordDto>(ex.Message, "OPERATION_IN_PROGRESS"));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<BackupRecordDto>(ex.Message, ex.ErrorCode));
            }
        })
        .RequirePermission(Permission.CreateBackup);

        // GET /api/v1/backups - List backup history (Owner/Admin only)
        group.MapGet("", async (IBackupService backupService, CancellationToken ct) =>
        {
            var history = await backupService.GetBackupHistoryAsync(ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(history));
        })
        .RequirePermission(Permission.CreateBackup);

        // GET /api/v1/backups/{id:guid} - Get backup details (Owner/Admin only)
        group.MapGet("/{id:guid}", async (Guid id, IBackupService backupService, CancellationToken ct) =>
        {
            var record = await backupService.GetBackupByIdAsync(id, ct).ConfigureAwait(false);
            if (record == null)
            {
                return Results.NotFound(ApiResponse.Fail<BackupRecordDto>("Backup record not found.", "NOT_FOUND"));
            }
            return Results.Ok(ApiResponse.Ok(record));
        })
        .RequirePermission(Permission.CreateBackup);

        // POST /api/v1/backups/verify - Verify backup file (Owner/Admin only)
        group.MapPost("/verify", async (
            RestoreBackupRequestDto request,
            IBackupService backupService,
            CancellationToken ct) =>
        {
            var report = await backupService.VerifyBackupAsync(request.FileName, ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(report));
        })
        .RequirePermission(Permission.CreateBackup);

        // POST /api/v1/backups/upload - Ingest external backup (Owner/Admin only)
        group.MapPost("/upload", async (
            IFormFile? dbFile,
            IFormFile? manifestFile,
            IBackupService backupService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (dbFile == null || manifestFile == null)
            {
                return Results.UnprocessableEntity(ApiResponse.Fail<BackupRecordDto>("Both dbFile and manifestFile are required.", "MISSING_FILES"));
            }

            var userId = httpContext.Items["CurrentUserId"]?.ToString()
                ?? throw new InvalidOperationException("CurrentUserId is missing from authenticated context.");
            var userName = (string?)httpContext.Items["CurrentUsername"]
                ?? throw new InvalidOperationException("CurrentUsername is missing from authenticated context.");

            try
            {
                using var dbStream = dbFile.OpenReadStream();
                using var manifestStream = manifestFile.OpenReadStream();

                var record = await backupService.IngestExternalBackupAsync(
                    dbStream,
                    dbFile.FileName,
                    manifestStream,
                    manifestFile.FileName,
                    userId,
                    userName,
                    ct).ConfigureAwait(false);

                return Results.Ok(ApiResponse.Ok(record, "External backup ingested successfully."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "BACKUP_FILENAME_COLLISION" || ex.ErrorCode == "BACKUP_IDENTITY_COLLISION")
            {
                return Results.Conflict(ApiResponse.Fail<BackupRecordDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex) when (ex.ErrorCode == "MANIFEST_FILENAME_MISMATCH" ||
                                             ex.ErrorCode == "STORE_ID_MISMATCH" ||
                                             ex.ErrorCode == "INVALID_SIGNATURE" ||
                                             ex.ErrorCode == "CHECKSUM_MISMATCH" ||
                                             ex.ErrorCode == "SIZE_MISMATCH" ||
                                             ex.ErrorCode == "INVALID_SQLITE_HEADER" ||
                                             ex.ErrorCode == "INTEGRITY_CHECK_FAILED" ||
                                             ex.ErrorCode == "FOREIGN_KEY_CHECK_FAILED" ||
                                             ex.ErrorCode == "INVALID_MANIFEST")
            {
                return Results.UnprocessableEntity(ApiResponse.Fail<BackupRecordDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex) when (ex.ErrorCode == "INVALID_FILENAME")
            {
                return Results.BadRequest(ApiResponse.Fail<BackupRecordDto>(ex.Message, ex.ErrorCode));
            }
        })
        .DisableAntiforgery()
        .RequirePermission(Permission.CreateBackup);

        // POST /api/v1/backups/restore - Restore backup (Owner only)
        group.MapPost("/restore", async (
            RestoreBackupRequestDto request,
            IBackupService backupService,
            IMaintenanceScopeFactory scopeFactory,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = httpContext.Items["CurrentUserId"]?.ToString()
                ?? throw new InvalidOperationException("CurrentUserId is missing from authenticated context.");
            var userName = (string?)httpContext.Items["CurrentUsername"]
                ?? throw new InvalidOperationException("CurrentUsername is missing from authenticated context.");

            try
            {
                using (scopeFactory.CreateMaintenanceScope())
                {
                    var record = await backupService.RestoreBackupAsync(request.FileName, userId, userName, ct).ConfigureAwait(false);
                    return Results.Ok(ApiResponse.Ok(record, "Database restored successfully."));
                }
            }
            catch (DomainException ex) when (ex.ErrorCode == "OPERATION_ALREADY_IN_PROGRESS")
            {
                return Results.Conflict(ApiResponse.Fail<BackupRecordDto>(ex.Message, "OPERATION_IN_PROGRESS"));
            }
            catch (DomainException ex) when (ex.ErrorCode == "BACKUP_NOT_FOUND")
            {
                return Results.NotFound(ApiResponse.Fail<BackupRecordDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<BackupRecordDto>(ex.Message, ex.ErrorCode));
            }
        })
        .RequirePermission(Permission.RestoreDatabase);

        // GET /api/v1/backups/settings - Get settings (Owner/Admin only)
        group.MapGet("/settings", async (IBackupService backupService, CancellationToken ct) =>
        {
            var settings = await backupService.GetSettingsAsync(ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(settings));
        })
        .RequirePermission(Permission.CreateBackup);

        // PUT /api/v1/backups/settings - Update settings (Owner/Admin only)
        group.MapPut("/settings", async (
            UpdateBackupSettingsDto request,
            IBackupService backupService,
            CancellationToken ct) =>
        {
            var settings = await backupService.UpdateSettingsAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(settings, "Backup settings updated successfully."));
        })
        .RequirePermission(Permission.CreateBackup);

        // POST /api/v1/backups/export-dr - Export encrypted portable DR package (Owner/Admin only)
        group.MapPost("/export-dr", async (
            ExportDisasterRecoveryRequestDto request,
            IBackupService backupService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.StoreMasterRecoveryKey) || request.StoreMasterRecoveryKey.Length != 64)
            {
                return Results.BadRequest(ApiResponse.Fail<DisasterRecoveryExportResultDto>("StoreMasterRecoveryKey must be a 64-character hexadecimal key.", "INVALID_MASTER_KEY"));
            }

            var keyChars = request.StoreMasterRecoveryKey.ToCharArray();
            try
            {
                var result = await backupService.ExportDisasterRecoveryAsync(
                    keyChars,
                    request.DestinationPath,
                    ct).ConfigureAwait(false);

                return Results.Ok(ApiResponse.Ok(result, "Disaster recovery export completed successfully."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "INVALID_MASTER_KEY")
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (DomainException ex) when (ex.ErrorCode == "OPERATION_ALREADY_IN_PROGRESS")
            {
                return Results.Conflict(ApiResponse.Fail<DisasterRecoveryExportResultDto>(ex.Message, "OPERATION_IN_PROGRESS"));
            }
            catch (DomainException ex) when (ex.ErrorCode == "SOURCE_BACKUP_NOT_FOUND")
            {
                return Results.NotFound(ApiResponse.Fail<DisasterRecoveryExportResultDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex) when (ex.ErrorCode == "INTEGRITY_CHECK_FAILED" || ex.ErrorCode == "CHECKSUM_MISMATCH")
            {
                return Results.UnprocessableEntity(ApiResponse.Fail<DisasterRecoveryExportResultDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<DisasterRecoveryExportResultDto>(ex.Message, ex.ErrorCode));
            }
            finally
            {
                Array.Clear(keyChars, 0, keyChars.Length);
            }
        })
        .RequirePermission(Permission.CreateBackup);

        // POST /api/v1/backups/bare-metal-recovery - Execute crash-safe bare-metal recovery (Owner only, loopback required)
        group.MapPost("/bare-metal-recovery", async (
            BareMetalRecoveryRequestDto request,
            IBackupService backupService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (remoteIp != null && !RetailPOS.Server.Middleware.LoopbackRecoveryMiddleware.IsLoopbackAddress(remoteIp))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.ConfirmationPhrase) ||
                !string.Equals(request.ConfirmationPhrase.Trim(), BareMetalRecoveryRequestDto.RequiredConfirmationPhrase, StringComparison.Ordinal))
            {
                return Results.BadRequest(ApiResponse.Fail<BareMetalRecoveryResultDto>($"Exact confirmation phrase '{BareMetalRecoveryRequestDto.RequiredConfirmationPhrase}' is required.", "CONFIRMATION_PHRASE_INVALID"));
            }

            if (string.IsNullOrWhiteSpace(request.SourceBackupPath))
            {
                return Results.BadRequest(ApiResponse.Fail<BareMetalRecoveryResultDto>("SourceBackupPath is required.", "PATH_REQUIRED"));
            }

            if (string.IsNullOrWhiteSpace(request.StoreMasterRecoveryKey) || request.StoreMasterRecoveryKey.Length != 64)
            {
                return Results.BadRequest(ApiResponse.Fail<BareMetalRecoveryResultDto>("StoreMasterRecoveryKey must be a 64-character hexadecimal key.", "INVALID_MASTER_KEY"));
            }

            var keyChars = request.StoreMasterRecoveryKey.ToCharArray();
            try
            {
                var result = await backupService.ExecuteBareMetalRecoveryAsync(
                    request.SourceBackupPath,
                    keyChars,
                    request.ConfirmationPhrase,
                    ct).ConfigureAwait(false);

                return Results.Ok(ApiResponse.Ok(result, "Bare-metal disaster recovery completed successfully."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "INVALID_MASTER_KEY")
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (DomainException ex) when (ex.ErrorCode == "OPERATION_ALREADY_IN_PROGRESS")
            {
                return Results.Conflict(ApiResponse.Fail<BareMetalRecoveryResultDto>(ex.Message, "OPERATION_IN_PROGRESS"));
            }
            catch (DomainException ex) when (ex.ErrorCode == "DATABASE_NOT_FOUND" || ex.ErrorCode == "PACKAGE_NOT_FOUND" || ex.ErrorCode == "FILE_NOT_FOUND")
            {
                return Results.NotFound(ApiResponse.Fail<BareMetalRecoveryResultDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex) when (ex.ErrorCode == "INTEGRITY_CHECK_FAILED" || ex.ErrorCode == "RECONSTRUCTION_FAILED" || ex.ErrorCode == "CHECKSUM_MISMATCH")
            {
                return Results.UnprocessableEntity(ApiResponse.Fail<BareMetalRecoveryResultDto>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<BareMetalRecoveryResultDto>(ex.Message, ex.ErrorCode));
            }
            finally
            {
                Array.Clear(keyChars, 0, keyChars.Length);
            }
        })
        .RequirePermission(Permission.RestoreDatabase);

        return app;
    }
}
