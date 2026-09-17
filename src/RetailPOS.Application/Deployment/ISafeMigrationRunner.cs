using System;
using System.Threading.Tasks;
using RetailPOS.Contracts.Deployment;

namespace RetailPOS.Application.Deployment;

public interface ISafeMigrationRunner
{
    Task<MigrationResult> RunSafeMigrationAsync(string operationId, Func<Task> executeMigrationAction);
    Task<RollbackResult> PerformStartupRecoveryIfNeededAsync();
}
