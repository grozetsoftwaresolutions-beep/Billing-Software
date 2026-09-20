using System;
using System.Collections.Generic;
using System.Linq;
using RetailPOS.Contracts.Common;
using RetailPOS.Infrastructure.Deployment.Steps;

namespace RetailPOS.Infrastructure.Deployment;

/// <summary>
/// Authoritative registry of schema migration steps.
/// Enforces version sequencing, dependency rules, and target schema versions.
/// </summary>
public sealed class SchemaMigrationRegistry
{
    public const int BaselineVersion = ProtocolVersionConstants.BaselineSchemaVersion; // 14
    public const int TargetVersion = ProtocolVersionConstants.TargetSchemaVersion;     // 15

    private readonly List<IMigrationStep> _steps;

    public SchemaMigrationRegistry(IEnumerable<IMigrationStep>? steps = null)
    {
        _steps = steps != null && steps.Any()
            ? steps.OrderBy(s => s.Version).ToList()
            : [new MigrationStep15_SchemaMigrationsTable()];

        ValidateOrder();
    }

    public int TargetSchemaVersion => _steps.Count > 0 ? _steps.Max(s => s.Version) : TargetVersion;

    public IReadOnlyList<IMigrationStep> GetAllSteps() => _steps.AsReadOnly();

    public IReadOnlyList<IMigrationStep> GetPendingSteps(int currentVersion)
    {
        return _steps.Where(s => s.Version > currentVersion).ToList();
    }

    private void ValidateOrder()
    {
        int prev = BaselineVersion;
        foreach (var step in _steps)
        {
            if (step.Version <= BaselineVersion)
            {
                throw new InvalidOperationException(
                    $"Migration step '{step.Name}' has version {step.Version} which is less than or equal to baseline {BaselineVersion}.");
            }
            if (step.Version != prev + 1)
            {
                throw new InvalidOperationException(
                    $"Migration step sequence broken: expected version {prev + 1}, found {step.Version} ('{step.Name}').");
            }
            prev = step.Version;
        }
    }
}
