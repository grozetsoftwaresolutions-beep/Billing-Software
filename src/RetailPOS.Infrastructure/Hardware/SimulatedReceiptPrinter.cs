using System.Text;
using RetailPOS.Application.Hardware;
using RetailPOS.Domain.Hardware;

namespace RetailPOS.Infrastructure.Hardware;

public sealed record SimulatedPrintJob(
    byte[] RawBytes,
    string AsciiPreview,
    DateTime PrintedAtUtc);

public sealed class SimulatedReceiptPrinter : IReceiptPrinter
{
    private readonly List<SimulatedPrintJob> _jobs = [];
    private readonly object _lock = new();

    public bool SimulateOffline { get; set; }
    public bool SimulateOutOfPaper { get; set; }

    public IReadOnlyList<SimulatedPrintJob> PrintJobs
    {
        get
        {
            lock (_lock)
            {
                return _jobs.ToList().AsReadOnly();
            }
        }
    }

    public Task<PrintResult> PrintRawBytesAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SimulateOffline)
            return Task.FromResult(PrintResult.Fail("Printer is offline or unplugged."));

        if (SimulateOutOfPaper)
            return Task.FromResult(PrintResult.Fail("Printer is out of paper."));

        var preview = Encoding.ASCII.GetString(bytes);
        lock (_lock)
        {
            _jobs.Add(new SimulatedPrintJob(bytes, preview, DateTime.UtcNow));
        }

        return Task.FromResult(PrintResult.Ok());
    }

    public Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (SimulateOffline)
            return Task.FromResult(DeviceStatusResult.Offline("Printer disconnected"));

        if (SimulateOutOfPaper)
            return Task.FromResult(DeviceStatusResult.Offline("Paper out"));

        return Task.FromResult(DeviceStatusResult.Online());
    }

    public void Clear()
    {
        lock (_lock)
        {
            _jobs.Clear();
        }
    }

    public string GetLastReceiptAscii()
    {
        lock (_lock)
        {
            return _jobs.Count > 0 ? _jobs[^1].AsciiPreview : string.Empty;
        }
    }

    public byte[] GetLastReceiptBytes()
    {
        lock (_lock)
        {
            return _jobs.Count > 0 ? _jobs[^1].RawBytes : [];
        }
    }
}
