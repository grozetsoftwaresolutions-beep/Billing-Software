using RetailPOS.Application.Hardware;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Hardware;

namespace RetailPOS.Client.Hardware;

public sealed class TerminalHardwareCoordinator : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _printerLock = new(1, 1);
    private readonly SemaphoreSlim _scaleLock = new(1, 1);
    private readonly object _stateLock = new();

    private IReceiptPrinter _printer;
    private ICashDrawer _drawer;
    private IWeighingScale _scale;
    private KeyboardWedgeListener _scanner;

    public Guid TerminalId { get; private set; } = Guid.Empty;
    public TerminalHardwareConfigDto? ActiveConfig { get; private set; }
    public bool IsInitialized { get; private set; }

    public KeyboardWedgeListener Scanner => _scanner;
    public IReceiptPrinter Printer => _printer;
    public ICashDrawer Drawer => _drawer;
    public IWeighingScale Scale => _scale;

    public TerminalHardwareCoordinator(
        IReceiptPrinter? printer = null,
        ICashDrawer? drawer = null,
        IWeighingScale? scale = null)
    {
        _printer = printer ?? new SimulatedReceiptPrinter();
        _drawer = drawer ?? new SimulatedCashDrawer();
        _scale = scale ?? new SimulatedWeighingScale();
        _scanner = new KeyboardWedgeListener();
    }

    public void Initialize(TerminalHardwareConfigDto config)
    {
        lock (_stateLock)
        {
            TerminalId = config.TerminalId;
            ActiveConfig = config;

            // Configure receipt printer
            _printer = config.PrinterConnectionType switch
            {
                PrinterConnectionType.WindowsSpooler => new WindowsSpoolerReceiptPrinter(config.PrinterNameOrAddress),
                PrinterConnectionType.NetworkTcp => new NetworkTcpReceiptPrinter(config.PrinterNameOrAddress, 9100, config.PrinterTimeoutMs),
                _ => new SimulatedReceiptPrinter()
            };

            // Configure cash drawer
            _drawer = config.DrawerConnectionType switch
            {
                DrawerConnectionType.PrinterDriven => new PrinterDrivenCashDrawer(_printer, config.DrawerKickPin),
                _ => new SimulatedCashDrawer()
            };

            // Configure weighing scale
            _scale = new SimulatedWeighingScale();

            // Configure scanner listener
            _scanner.Dispose();
            _scanner = new KeyboardWedgeListener(config.ScannerMaxInterKeyMs);

            IsInitialized = true;
        }
    }

    public async Task<PrintResult> PrintReceiptAsync(byte[] escPosBytes, CancellationToken ct = default)
    {
        await _printerLock.WaitAsync(ct);
        try
        {
            return await _printer.PrintRawBytesAsync(escPosBytes, ct);
        }
        finally
        {
            _printerLock.Release();
        }
    }

    public async Task<DrawerKickResult> OpenDrawerAsync(string reason, CancellationToken ct = default)
    {
        await _printerLock.WaitAsync(ct);
        try
        {
            return await _drawer.OpenDrawerAsync(reason, ct);
        }
        finally
        {
            _printerLock.Release();
        }
    }

    public async Task<ScaleWeightReadingResult> ReadScaleWeightAsync(CancellationToken ct = default)
    {
        await _scaleLock.WaitAsync(ct);
        try
        {
            return await _scale.ReadWeightAsync(ct);
        }
        finally
        {
            _scaleLock.Release();
        }
    }

    public async Task<bool> ZeroScaleAsync(CancellationToken ct = default)
    {
        await _scaleLock.WaitAsync(ct);
        try
        {
            return await _scale.ZeroAsync(ct);
        }
        finally
        {
            _scaleLock.Release();
        }
    }

    public async Task<bool> TareScaleAsync(CancellationToken ct = default)
    {
        await _scaleLock.WaitAsync(ct);
        try
        {
            return await _scale.TareAsync(ct);
        }
        finally
        {
            _scaleLock.Release();
        }
    }

    public void Dispose()
    {
        _printerLock.Dispose();
        _scaleLock.Dispose();
        _scanner.Dispose();
        (_printer as IDisposable)?.Dispose();
        (_drawer as IDisposable)?.Dispose();
        (_scale as IDisposable)?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _printerLock.Dispose();
        _scaleLock.Dispose();
        _scanner.Dispose();

        if (_printer is IAsyncDisposable pAsync)
            await pAsync.DisposeAsync();
        else if (_printer is IDisposable pDisp)
            pDisp.Dispose();

        if (_drawer is IAsyncDisposable dAsync)
            await dAsync.DisposeAsync();
        else if (_drawer is IDisposable dDisp)
            dDisp.Dispose();

        if (_scale is IAsyncDisposable sAsync)
            await sAsync.DisposeAsync();
        else if (_scale is IDisposable sDisp)
            sDisp.Dispose();
    }
}
