using RetailPOS.Application.Hardware;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class SimulatedCashDrawer : ICashDrawer
{
    private readonly object _lock = new();
    public int KickCount { get; private set; }
    public string? LastKickReason { get; private set; }
    public bool IsOpen { get; private set; }
    public bool SimulateJammed { get; set; }

    public Task<DrawerKickResult> OpenDrawerAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SimulateJammed)
        {
            return Task.FromResult(DrawerKickResult.Fail("SimulatedDrawer", "Drawer is mechanically jammed or locked."));
        }

        lock (_lock)
        {
            KickCount++;
            LastKickReason = reason;
            IsOpen = true;
        }

        return Task.FromResult(DrawerKickResult.Ok("SimulatedDrawer"));
    }

    public void CloseDrawer()
    {
        lock (_lock)
        {
            IsOpen = false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            KickCount = 0;
            LastKickReason = null;
            IsOpen = false;
            SimulateJammed = false;
        }
    }
}
