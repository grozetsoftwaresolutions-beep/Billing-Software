using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.Infrastructure.Deployment;

/// <summary>
/// Defines an atomic, idempotent database schema migration step.
/// </summary>
public interface IMigrationStep
{
    /// <summary>
    /// Monotonically increasing schema version number.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Descriptive name of the migration step.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Applies DDL and DML changes within an active database transaction.
    /// </summary>
    Task ApplyAsync(RetailDbContext context, CancellationToken ct = default);
}
