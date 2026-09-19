using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security.Srp6a;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Server.Middleware;
using System.Security.Cryptography;


namespace RetailPOS.Server.Endpoints;

public static class TerminalEndpoints
{
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/terminals");

        // Token Generation Endpoints (Role Protected)
        group.MapPost("/tokens/enrollment", async (
            EnrollmentTokenRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
            var result = await enrollmentService.GenerateEnrollmentTokenAsync(request, currentUserId, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GenerateEnrollmentToken")
        .WithSummary("Generates an enrollment token for a new terminal.");

        group.MapPost("/tokens/repair", async (
            EnrollmentTokenRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
            var repairRequest = request with { Purpose = EnrollmentTokenPurpose.ExistingTerminalRepair };
            var result = await enrollmentService.GenerateEnrollmentTokenAsync(repairRequest, currentUserId, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GenerateRepairToken")
        .WithSummary("Generates a repair token for an existing terminal.");

        group.MapPost("/tokens/re-pinning", async (
            RePinningTokenRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
            var result = await enrollmentService.GenerateRePinningTokenAsync(request, currentUserId, ct);
            return Results.Ok(ApiResponse.Ok(result));
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("GenerateRePinningToken")
        .WithSummary("Generates a re-pinning token for disaster recovery server replacement.");

        // Anonymous Terminal Lifecycle Operations
        group.MapPost("/enroll", async (
            TerminalEnrollRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var clientIp = context.Connection.RemoteIpAddress?.ToString();
                var enrollRequest = request with { ClientIpAddress = clientIp ?? request.ClientIpAddress };
                var result = await enrollmentService.EnrollTerminalAsync(enrollRequest, ct);
                return Results.Ok(ApiResponse.Ok(result));
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("EnrollTerminal")
        .WithSummary("Enrolls a new POS terminal using a single-use token.");

        group.MapPost("/repair", async (
            TerminalRepairRequestDto request,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var clientIp = context.Connection.RemoteIpAddress?.ToString();
                var result = await enrollmentService.RepairTerminalAsync(request.TerminalId, request.RepairToken, clientIp ?? request.ClientIpAddress, ct);
                return Results.Ok(ApiResponse.Ok(result));
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("RepairTerminal")
        .WithSummary("Repairs an existing terminal with a repair token.");

        group.MapPost("/re-pin", async (
            RePinningRequestDto request,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var result = await enrollmentService.RePinTerminalAsync(request, ct);
                return Results.Ok(result);
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("RePinTerminal")
        .WithSummary("Re-pins server identity post-disaster recovery.");

        group.MapPost("/heartbeat", async (
            TerminalHeartbeatDto request,
            HttpContext context,
            ITerminalHeartbeatTracker heartbeatTracker,
            CancellationToken ct) =>
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString();
            var response = await heartbeatTracker.RecordHeartbeatAsync(request with { ClientIpAddress = clientIp }, ct);
            return Results.Ok(response);
        })
        .WithName("TerminalHeartbeat")
        .WithSummary("Records periodic terminal heartbeat.");

        // Terminal Query and Management (Role Protected)
        group.MapGet("/", async (
            ITerminalService terminalService,
            CancellationToken ct) =>
        {
            var terminals = await terminalService.GetTerminalsAsync(ct);
            return Results.Ok(ApiResponse.Ok(terminals));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetTerminals")
        .WithSummary("Lists registered POS terminals.");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var detail = await enrollmentService.GetTerminalDetailAsync(id, ct);
            if (detail is null)
                return Results.NotFound(ApiResponse.Fail<object>("Terminal not found.", "TERMINAL_NOT_FOUND"));

            return Results.Ok(ApiResponse.Ok(detail));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("GetTerminalById")
        .WithSummary("Gets details of a specific POS terminal.");

        group.MapPost("/", async (
            RegisterTerminalRequest request,
            HttpContext context,
            ITerminalService terminalService,
            CancellationToken ct) =>
        {
            var executingUsername = (string)context.Items["CurrentUsername"]!;
            var result = await terminalService.RegisterTerminalAsync(request, executingUsername, ct);
            return Results.Ok(result);
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("RegisterTerminal")
        .WithSummary("Registers a new POS terminal counter.");

        group.MapPut("/{id:guid}/status", async (
            Guid id,
            bool? isActive,
            TerminalStatusUpdateDto? request,
            HttpContext context,
            ITerminalService terminalService,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            var executingUsername = (string)context.Items["CurrentUsername"]!;
            var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;

            if (isActive.HasValue)
            {
                var result = await terminalService.SetTerminalStatusAsync(id, isActive.Value, executingUsername, ct);
                return Results.Ok(result);
            }

            if (request != null)
            {
                try
                {
                    await enrollmentService.UpdateTerminalStatusAsync(id, request.Status, currentUserId, ct);
                    return Results.Ok(ApiResponse.Ok(new { TerminalId = id, request.Status }, "Terminal status updated."));
                }
                catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_DECOMMISSIONED" || ex.ErrorCode == "TERMINAL_ALREADY_DECOMMISSIONED")
                {
                    return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
                }
                catch (DomainException ex)
                {
                    return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
                }
            }

            return Results.BadRequest(ApiResponse.Fail<object>("Status parameter is required.", "INVALID_REQUEST"));
        })
        .RequireRole(RoleType.Manager, RoleType.Admin, RoleType.Owner)
        .WithName("SetTerminalStatus")
        .WithSummary("Updates terminal operational status.");

        group.MapPost("/{id:guid}/decommission", async (
            Guid id,
            HttpContext context,
            ITerminalEnrollmentService enrollmentService,
            CancellationToken ct) =>
        {
            try
            {
                var currentUserId = context.Items["CurrentUserId"]?.ToString() ?? string.Empty;
                await enrollmentService.DecommissionTerminalAsync(id, currentUserId, ct);
                return Results.Ok(ApiResponse.Ok(new { TerminalId = id, Status = TerminalStatus.Decommissioned }, "Terminal permanently decommissioned."));
            }
            catch (DomainException ex) when (ex.ErrorCode == "TERMINAL_ALREADY_DECOMMISSIONED")
            {
                return Results.Conflict(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .RequireRole(RoleType.Admin, RoleType.Owner)
        .WithName("DecommissionTerminal")
        .WithSummary("Permanently decommissions a POS terminal.");

        // SRP-6a Pairing Endpoints
        var pairingGroup = endpoints.MapGroup("/api/v1/terminals/pairing");

        pairingGroup.MapPost("/create-session", async (
            CreatePairingSessionRequestDto request,
            HttpContext context,
            ISrp6aPairingService pairingService,
            IStoreIdentityService storeIdentityService,
            CancellationToken ct) =>
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            var isLoopback = remoteIp != null && LoopbackRecoveryMiddleware.IsLoopbackAddress(remoteIp);
            var hasRole = context.Items.TryGetValue("CurrentUserRole", out var roleObj) &&
                (roleObj is RoleType.Owner || roleObj is RoleType.Admin);

            if (!isLoopback && !hasRole)
            {
                return Results.Json(
                    ApiResponse.Fail<object>("Pairing session creation requires Owner/Admin permissions or loopback access.", "PERMISSION_DENIED"),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var storeId = storeIdentityService.StoreId;
            var (pairingOpId, code, salt) = await pairingService.CreatePairingSessionAsync(storeId, request.TerminalId, ct);
            return Results.Ok(ApiResponse.Ok(new CreatePairingSessionResponseDto(pairingOpId, code, Convert.ToHexString(salt))));
        })
        .WithName("CreatePairingSession")
        .WithSummary("Creates an SRP-6a terminal pairing session.");

        pairingGroup.MapGet("/pending", async (
            HttpContext context,
            IPairingTokenRepository pairingRepo,
            CancellationToken ct) =>
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            var isLoopback = remoteIp != null && LoopbackRecoveryMiddleware.IsLoopbackAddress(remoteIp);
            var hasRole = context.Items.TryGetValue("CurrentUserRole", out var roleObj) &&
                (roleObj is RoleType.Owner || roleObj is RoleType.Admin);

            if (!isLoopback && !hasRole)
            {
                return Results.Json(
                    ApiResponse.Fail<object>("Viewing pending pairings requires Owner/Admin permissions or loopback access.", "PERMISSION_DENIED"),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var pending = await pairingRepo.GetPendingAsync(ct);
            return Results.Ok(ApiResponse.Ok(pending));
        })
        .WithName("GetPendingPairingSessions")
        .WithSummary("Lists pending SRP-6a terminal pairing sessions.");

        pairingGroup.MapGet("/challenge/{pairingOperationId:guid}", async (
            Guid pairingOperationId,
            IPairingTokenRepository pairingRepo,
            CancellationToken ct) =>
        {
            var token = await pairingRepo.GetByIdAsync(pairingOperationId, ct);
            if (token == null)
            {
                return Results.NotFound(ApiResponse.Fail<object>("Pairing session not found.", "PAIRING_NOT_FOUND"));
            }

            return Results.Ok(ApiResponse.Ok(new PairingChallengeResponseDto(
                token.PairingOperationId,
                token.StoreId,
                token.TerminalId,
                Convert.ToHexString(token.Salt))));
        })
        .WithName("GetPairingChallenge")
        .WithSummary("Retrieves salt and IDs for SRP-6a pairing.");

        pairingGroup.MapPost("/initiate", async (
            PairingInitiateRequestDto request,
            ISrp6aPairingService pairingService,
            CancellationToken ct) =>
        {
            try
            {
                var clientA = Convert.FromBase64String(request.ClientEphemeralABase64);
                var serverB = await pairingService.InitiatePairingAsync(request.PairingOperationId, clientA, ct);
                return Results.Ok(ApiResponse.Ok(new PairingInitiateResponseDto(
                    request.PairingOperationId,
                    Convert.ToBase64String(serverB))));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("InitiatePairing")
        .WithSummary("Initiates SRP-6a ephemeral exchange.");

        pairingGroup.MapPost("/confirm", async (
            PairingConfirmRequestDto request,
            ISrp6aPairingService pairingService,
            IServerIdentityService serverIdentityService,
            CancellationToken ct) =>
        {
            try
            {
                var serverIdentity = await serverIdentityService.GetServerIdentityAsync(ct)
                    ?? throw new DomainException("Server identity not available.", "SERVER_IDENTITY_NOT_AVAILABLE");
                var clientM1 = Convert.FromBase64String(request.ClientEvidenceM1Base64);
                var retryToken = Convert.FromBase64String(request.RetryAuthTokenBase64);
                var tmkBytes = RandomNumberGenerator.GetBytes(32);

                var result = await pairingService.ConfirmPairingAsync(
                    request.PairingOperationId,
                    clientM1,
                    request.SASConfirmationCode,
                    request.CandidateF1,
                    retryToken,
                    tmkBytes,
                    serverIdentity.ServerFingerprint,
                    serverIdentity.PublicKeySpki,
                    ct);

                return Results.Ok(ApiResponse.Ok(new PairingConfirmResponseDto(
                    request.PairingOperationId,
                    result.ServerEvidenceM2,
                    result.EncryptedTmk,
                    result.PublicKeySpki)));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("ConfirmPairing")
        .WithSummary("Confirms SRP-6a client evidence and returns TMK.");

        pairingGroup.MapPost("/retry", async (
            PairingRetryRequestDto request,
            ISrp6aPairingService pairingService,
            IServerIdentityService serverIdentityService,
            CancellationToken ct) =>
        {
            try
            {
                var serverIdentity = await serverIdentityService.GetServerIdentityAsync(ct)
                    ?? throw new DomainException("Server identity not available.", "SERVER_IDENTITY_NOT_AVAILABLE");
                var retryToken = Convert.FromBase64String(request.RetryAuthTokenBase64);

                var result = await pairingService.RetryPairingAsync(
                    request.PairingOperationId,
                    request.StoreId,
                    request.TerminalId,
                    request.CandidateF1,
                    retryToken,
                    serverIdentity.ServerFingerprint,
                    serverIdentity.PublicKeySpki,
                    ct);

                return Results.Ok(ApiResponse.Ok(new PairingConfirmResponseDto(
                    request.PairingOperationId,
                    result.ServerEvidenceM2,
                    result.EncryptedTmk,
                    result.PublicKeySpki)));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("RetryPairing")
        .WithSummary("Retries pairing using retry token.");

        pairingGroup.MapPost("/finalize", async (
            PairingFinalizeRequestDto request,
            ISrp6aPairingService pairingService,
            CancellationToken ct) =>
        {
            try
            {
                var proofBytes = Convert.FromBase64String(request.FinalizeProofBase64);
                await pairingService.FinalizePairingAsync(request.PairingOperationId, proofBytes, null, ct);
                return Results.Ok(ApiResponse.Ok(new { Message = "Pairing successfully finalized." }));
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(ApiResponse.Fail<object>(ex.Message, ex.ErrorCode));
            }
        })
        .WithName("FinalizePairing")
        .WithSummary("Finalizes terminal pairing operation.");

        return endpoints;
    }
}

