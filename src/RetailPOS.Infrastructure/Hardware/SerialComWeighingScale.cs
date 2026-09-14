using System.IO.Ports;
using System.Text;
using RetailPOS.Application.Hardware;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class SerialComWeighingScale : IWeighingScale, IDisposable, IAsyncDisposable
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly int _dataBits;
    private readonly Parity _parity;
    private readonly StopBits _stopBits;
    private readonly IScaleProtocolParser _parser;
    private readonly int _timeoutMs;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private SerialPort? _serialPort;
    private bool _disposed;

    public SerialComWeighingScale(
        string portName,
        int baudRate = 9600,
        int dataBits = 8,
        Parity parity = Parity.None,
        StopBits stopBits = StopBits.One,
        IScaleProtocolParser? parser = null,
        int timeoutMs = 2000)
    {
        _portName = string.IsNullOrWhiteSpace(portName) ? "COM1" : portName.Trim();
        _baudRate = baudRate > 0 ? baudRate : 9600;
        _dataBits = dataBits > 0 ? dataBits : 8;
        _parity = parity;
        _stopBits = stopBits;
        _parser = parser ?? new GenericAsciiScaleParser();
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 2000;
    }

    private void EnsurePortOpen()
    {
        if (_serialPort != null && _serialPort.IsOpen)
            return;

        _serialPort?.Dispose();
        _serialPort = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
        {
            ReadTimeout = _timeoutMs,
            WriteTimeout = _timeoutMs,
            NewLine = "\r\n"
        };
        _serialPort.Open();
        _serialPort.DiscardInBuffer();
        _serialPort.DiscardOutBuffer();
    }

    public async Task<ScaleWeightReadingResult> ReadWeightAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return ScaleWeightReadingResult.Fail("Scale device disposed.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeoutMs);

        await _semaphore.WaitAsync(cts.Token);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    EnsurePortOpen();

                    var line = _serialPort!.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        return ScaleWeightReadingResult.Fail("Empty frame received from scale.");
                    }

                    var bytes = Encoding.ASCII.GetBytes(line);
                    var parseResult = _parser.ParseFrame(bytes);
                    if (!parseResult.Success)
                    {
                        return ScaleWeightReadingResult.Fail(parseResult.ErrorMessage ?? "Failed to parse scale frame.");
                    }

                    return ScaleWeightReadingResult.Ok(
                        parseResult.Weight,
                        parseResult.Unit,
                        parseResult.IsStable,
                        parseResult.IsZero,
                        parseResult.IsTare,
                        parseResult.RawReading);
                }
                catch (TimeoutException)
                {
                    return ScaleWeightReadingResult.Fail($"Timeout reading from scale on {_portName} ({_timeoutMs}ms).");
                }
                catch (UnauthorizedAccessException ex)
                {
                    return ScaleWeightReadingResult.Fail($"Access denied to serial port {_portName}: {ex.Message}");
                }
                catch (IOException ex)
                {
                    _serialPort?.Dispose();
                    _serialPort = null;
                    return ScaleWeightReadingResult.Fail($"IO error on serial port {_portName}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    return ScaleWeightReadingResult.Fail($"Scale error on {_portName}: {ex.Message}");
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return ScaleWeightReadingResult.Fail($"Scale reading timed out or was cancelled ({_timeoutMs}ms).");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> ZeroAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeoutMs);

        await _semaphore.WaitAsync(cts.Token);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    EnsurePortOpen();
                    _serialPort!.WriteLine("Z");
                    return true;
                }
                catch
                {
                    return false;
                }
            }, cts.Token);
        }
        catch
        {
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> TareAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeoutMs);

        await _semaphore.WaitAsync(cts.Token);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    EnsurePortOpen();
                    _serialPort!.WriteLine("T");
                    return true;
                }
                catch
                {
                    return false;
                }
            }, cts.Token);
        }
        catch
        {
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return DeviceStatusResult.Offline("Disposed");

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var ports = SerialPort.GetPortNames();
            if (!ports.Contains(_portName, StringComparer.OrdinalIgnoreCase))
            {
                return DeviceStatusResult.Offline($"Port {_portName} not found");
            }
            return DeviceStatusResult.Online($"Ready on {_portName}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
            }
            _serialPort?.Dispose();
            _serialPort = null;
        }
        catch { }

        _semaphore.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
