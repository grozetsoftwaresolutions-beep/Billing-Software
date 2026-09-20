namespace RetailPOS.Application.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Contracts.Diagnostics;

public interface ISupportDiagnosticsService
{
    Task<DiagnosticBundleMetadataDto> GenerateSupportBundleAsync(string triggeredByUserId, string triggeredByUserName, string? reason = null, CancellationToken ct = default);
    Task<IReadOnlyList<DiagnosticBundleMetadataDto>> GetBundleHistoryAsync(CancellationToken ct = default);
    Task<Stream?> OpenBundleReadStreamAsync(Guid operationId, CancellationToken ct = default);
    Task<DiagnosticSystemEnvironmentDto> GetSystemEnvironmentAsync(CancellationToken ct = default);
    Task<DiagnosticServerHealthDto> GetServerHealthSummaryAsync(CancellationToken ct = default);
}
