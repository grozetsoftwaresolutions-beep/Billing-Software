using System.Runtime.InteropServices;
using RetailPOS.Application.Hardware;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class WindowsSpoolerReceiptPrinter : IReceiptPrinter, IDisposable
{
    private readonly string _printerName;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFOW
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pDatatype;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPWStr)] string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int Level, [In] ref DOCINFOW pDocInfo);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public WindowsSpoolerReceiptPrinter(string printerName)
    {
        _printerName = string.IsNullOrWhiteSpace(printerName) ? "Generic / Text Only" : printerName.Trim();
    }

    public async Task<PrintResult> PrintRawBytesAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PrintResult.Fail("Windows Print Spooler is only supported on Windows operating systems.");
        }

        if (bytes == null || bytes.Length == 0)
            return PrintResult.Ok();

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                if (!OpenPrinter(_printerName, out var hPrinter, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    return PrintResult.Fail($"Failed to open Windows printer '{_printerName}'. Win32 Error: {err}");
                }

                try
                {
                    var di = new DOCINFOW
                    {
                        pDocName = "Retail POS Receipt",
                        pDatatype = "RAW"
                    };

                    if (!StartDocPrinter(hPrinter, 1, ref di))
                    {
                        var err = Marshal.GetLastWin32Error();
                        return PrintResult.Fail($"StartDocPrinter failed for '{_printerName}'. Win32 Error: {err}");
                    }

                    try
                    {
                        if (!StartPagePrinter(hPrinter))
                        {
                            var err = Marshal.GetLastWin32Error();
                            return PrintResult.Fail($"StartPagePrinter failed for '{_printerName}'. Win32 Error: {err}");
                        }

                        var unmanagedBytes = Marshal.AllocHGlobal(bytes.Length);
                        try
                        {
                            Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                            if (!WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out var written))
                            {
                                var err = Marshal.GetLastWin32Error();
                                return PrintResult.Fail($"WritePrinter failed for '{_printerName}'. Win32 Error: {err}");
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(unmanagedBytes);
                        }

                        EndPagePrinter(hPrinter);
                    }
                    finally
                    {
                        EndDocPrinter(hPrinter);
                    }
                }
                finally
                {
                    ClosePrinter(hPrinter);
                }

                return PrintResult.Ok();
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return PrintResult.Fail($"Windows Print Spooler exception: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(DeviceStatusResult.Offline("Non-Windows OS"));

        if (!OpenPrinter(_printerName, out var hPrinter, IntPtr.Zero))
            return Task.FromResult(DeviceStatusResult.Offline($"Printer '{_printerName}' unreachable"));

        ClosePrinter(hPrinter);
        return Task.FromResult(DeviceStatusResult.Online($"Ready: {_printerName}"));
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
