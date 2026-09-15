namespace RetailPOS.Application.Services;

using System.Threading;
using System.Threading.Tasks;

public interface IFilesystemReconciliationEngine
{
    Task ReconcileStartupAsync(CancellationToken ct = default);
}
