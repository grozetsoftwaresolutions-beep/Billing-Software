using System.Net.Sockets;
using RetailPOS.Application.Hardware;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class NetworkTcpReceiptPrinter : IReceiptPrinter, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public NetworkTcpReceiptPrinter(string host, int port = 9100, int timeoutMs = 3000)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public async Task<PrintResult> PrintRawBytesAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (bytes == null || bytes.Length == 0)
            return PrintResult.Ok();

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeoutMs);

            await client.ConnectAsync(_host, _port, cts.Token);
            using var stream = client.GetStream();
            stream.WriteTimeout = _timeoutMs;
            await stream.WriteAsync(bytes, cts.Token);
            await stream.FlushAsync(cts.Token);

            return PrintResult.Ok();
        }
        catch (Exception ex)
        {
            return PrintResult.Fail($"Network printer '{_host}:{_port}' error: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(1500);
            await client.ConnectAsync(_host, _port, cts.Token);
            return DeviceStatusResult.Online($"Network printer {_host}:{_port} reachable");
        }
        catch (Exception ex)
        {
            return DeviceStatusResult.Offline($"Network printer unreachable: {ex.Message}");
        }
    }
    public void Dispose()
    {
        _semaphore.Dispose();
    }
}