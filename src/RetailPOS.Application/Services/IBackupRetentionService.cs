namespace RetailPOS.Application.Services;

using System.Threading;
using System.Threading.Tasks;

public interface IBackupRetentionService
{
    Task PruneAsync(CancellationToken ct = default);
}
