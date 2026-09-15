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

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/backups");

        // POST /api/v1/backups - Create manual backup
        group.MapPost("", async (
            CreateBackupRequestDto? request,
            IBackupService backupService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = httpContext.User.FindFirst("sub")?.Value ?? "SYSTEM";
            var userName = httpContext.User.Identity?.Name ?? "Administrator";

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
        });

        // GET /api/v1/backups - List backup history
        group.MapGet("", async (IBackupService backupService, CancellationToken ct) =>
        {
            var history = await backupService.GetBackupHistoryAsync(ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(history));
        });

        // GET /api/v1/backups/{id:guid} - Get backup details
        group.MapGet("/{id:guid}", async (Guid id, IBackupService backupService, CancellationToken ct) =>
        {
            var record = await backupService.GetBackupByIdAsync(id, ct).ConfigureAwait(false);
            if (record == null)
            {
                return Results.NotFound(ApiResponse.Fail<BackupRecordDto>("Backup record not found.", "NOT_FOUND"));
            }
            return Results.Ok(ApiResponse.Ok(record));
        });

        // POST /api/v1/backups/verify - Verify backup file
        group.MapPost("/verify", async (
            RestoreBackupRequestDto request,
            IBackupService backupService,
            CancellationToken ct) =>
        {
            var report = await backupService.VerifyBackupAsync(request.FileName, ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(report));
        });

        // POST /api/v1/backups/upload - Ingest external backup
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

            var userId = httpContext.User.FindFirst("sub")?.Value ?? "SYSTEM";
            var userName = httpContext.User.Identity?.Name ?? "Administrator";

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
        });

        // POST /api/v1/backups/restore - Restore backup
        group.MapPost("/restore", async (
            RestoreBackupRequestDto request,
            IBackupService backupService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var userId = httpContext.User.FindFirst("sub")?.Value ?? "SYSTEM";
            var userName = httpContext.User.Identity?.Name ?? "Administrator";

            try
            {
                var record = await backupService.RestoreBackupAsync(request.FileName, userId, userName, ct).ConfigureAwait(false);
                return Results.Ok(ApiResponse.Ok(record, "Database restored successfully."));
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
        });

        // GET /api/v1/backups/settings - Get settings
        group.MapGet("/settings", async (IBackupService backupService, CancellationToken ct) =>
        {
            var settings = await backupService.GetSettingsAsync(ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(settings));
        });

        // PUT /api/v1/backups/settings - Update settings
        group.MapPut("/settings", async (
            UpdateBackupSettingsDto request,
            IBackupService backupService,
            CancellationToken ct) =>
        {
            var settings = await backupService.UpdateSettingsAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(ApiResponse.Ok(settings, "Backup settings updated successfully."));
        });

        return app;
    }
}
