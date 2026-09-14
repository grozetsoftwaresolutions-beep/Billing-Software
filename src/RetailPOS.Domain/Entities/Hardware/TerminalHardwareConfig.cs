using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Entities.Hardware;

public sealed class TerminalHardwareConfig : BaseEntity<Guid>
{
    public Guid TerminalId => Id;
    public PrinterConnectionType PrinterConnectionType { get; private set; }
    public string PrinterNameOrAddress { get; private set; } = "SimulatedPrinter";
    public PaperWidth PaperWidth { get; private set; } = PaperWidth.Width80mm;
    public PrinterEncoding PrinterEncoding { get; private set; } = PrinterEncoding.Ascii;
    public int PrinterCodePage { get; private set; }
    public int PrinterTimeoutMs { get; private set; } = 5000;
    public bool AutoCut { get; private set; } = true;
    public bool OpenDrawerOnCashSale { get; private set; } = true;
    public DrawerConnectionType DrawerConnectionType { get; private set; } = DrawerConnectionType.Simulated;
    public int DrawerKickPin { get; private set; } // 0 = Pin 2, 1 = Pin 5
    public int DrawerPulseOnMs { get; private set; } = 50;
    public int DrawerPulseOffMs { get; private set; } = 500;
    public int ScannerMaxInterKeyMs { get; private set; } = 35;
    public ScaleConnectionType ScaleConnectionType { get; private set; } = ScaleConnectionType.Simulated;
    public string? ScalePortName { get; private set; }
    public int ScaleBaudRate { get; private set; } = 9600;
    public int ScaleDataBits { get; private set; } = 8;
    public int ScaleParity { get; private set; }
    public int ScaleStopBits { get; private set; } = 1;
    public ScaleProtocol ScaleProtocol { get; private set; } = ScaleProtocol.GenericAscii;
    public int ScaleTimeoutMs { get; private set; } = 2000;
    public bool IsScaleEnabled { get; private set; }

    private TerminalHardwareConfig() { }

    public TerminalHardwareConfig(Guid terminalId)
    {
        Id = terminalId;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdatePrinter(
        PrinterConnectionType connectionType,
        string printerNameOrAddress,
        PaperWidth paperWidth,
        PrinterEncoding encoding,
        int codePage,
        int timeoutMs,
        bool autoCut,
        bool openDrawerOnCash)
    {
        if (string.IsNullOrWhiteSpace(printerNameOrAddress))
            throw new DomainException("Printer name or address cannot be empty.", "INVALID_PRINTER_CONFIG");

        PrinterConnectionType = connectionType;
        PrinterNameOrAddress = printerNameOrAddress.Trim();
        PaperWidth = paperWidth;
        PrinterEncoding = encoding;
        PrinterCodePage = codePage;
        PrinterTimeoutMs = timeoutMs > 0 ? timeoutMs : 5000;
        AutoCut = autoCut;
        OpenDrawerOnCashSale = openDrawerOnCash;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateDrawer(
        DrawerConnectionType connectionType,
        int kickPin,
        int pulseOnMs,
        int pulseOffMs)
    {
        DrawerConnectionType = connectionType;
        DrawerKickPin = kickPin == 1 ? 1 : 0;
        DrawerPulseOnMs = pulseOnMs > 0 ? pulseOnMs : 50;
        DrawerPulseOffMs = pulseOffMs > 0 ? pulseOffMs : 500;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateScanner(int maxInterKeyMs)
    {
        ScannerMaxInterKeyMs = maxInterKeyMs > 0 ? maxInterKeyMs : 35;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateScale(
        ScaleConnectionType connectionType,
        string? portName,
        int baudRate,
        int dataBits,
        int parity,
        int stopBits,
        ScaleProtocol protocol,
        int timeoutMs,
        bool isEnabled)
    {
        ScaleConnectionType = connectionType;
        ScalePortName = portName?.Trim();
        ScaleBaudRate = baudRate > 0 ? baudRate : 9600;
        ScaleDataBits = dataBits >= 7 ? dataBits : 8;
        ScaleParity = parity;
        ScaleStopBits = stopBits;
        ScaleProtocol = protocol;
        ScaleTimeoutMs = timeoutMs > 0 ? timeoutMs : 2000;
        IsScaleEnabled = isEnabled;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
