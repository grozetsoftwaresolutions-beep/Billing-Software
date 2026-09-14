using RetailPOS.Client.Hardware;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Client.Tests;

public sealed class TerminalHardwareCoordinatorTests
{
    [Fact]
    public void KeyboardWedgeListener_NormalizesBarcodesCorrectly()
    {
        // 1. Standard EAN-13
        Assert.Equal("8901234567890", KeyboardWedgeListener.NormalizeBarcode("8901234567890"));

        // 2. AIM Symbology Prefix ]E0
        Assert.Equal("8901234567890", KeyboardWedgeListener.NormalizeBarcode("]E08901234567890"));

        // 3. STX (0x02) and ETX (0x03) stripping
        Assert.Equal("8901234567890", KeyboardWedgeListener.NormalizeBarcode("\x02]E08901234567890\x03"));

        // 4. Whitespace trimming
        Assert.Equal("8901234567890", KeyboardWedgeListener.NormalizeBarcode("  8901234567890  \r\n"));
    }

    [Fact]
    public void KeyboardWedgeListener_FiresEventOnCarriageReturn()
    {
        using var listener = new KeyboardWedgeListener(maxInterKeyMs: 35);
        BarcodeScannedEventArgs? eventArgs = null;
        listener.BarcodeScanned += (_, args) => eventArgs = args;

        listener.ProcessKeyString("]E089012345\r");

        Assert.NotNull(eventArgs);
        Assert.Equal("89012345", eventArgs.Barcode);
        Assert.Equal(8, eventArgs.Barcode.Length);
    }

    [Fact]
    public async Task TerminalHardwareCoordinator_InitializesAndPerformsHardwareOperations()
    {
        await using var coordinator = new TerminalHardwareCoordinator();
        var terminalId = Guid.NewGuid();

        var config = new TerminalHardwareConfigDto(
            TerminalId: terminalId,
            PrinterConnectionType: PrinterConnectionType.Simulated,
            PrinterNameOrAddress: "SimulatedPrinter",
            PaperWidth: PaperWidth.Width80mm,
            PrinterEncoding: PrinterEncoding.Ascii,
            PrinterCodePage: 0,
            PrinterTimeoutMs: 5000,
            AutoCut: true,
            OpenDrawerOnCashSale: true,
            DrawerConnectionType: DrawerConnectionType.Simulated,
            DrawerKickPin: 0,
            DrawerPulseOnMs: 50,
            DrawerPulseOffMs: 500,
            ScannerMaxInterKeyMs: 35,
            ScaleConnectionType: ScaleConnectionType.Simulated,
            ScalePortName: null,
            ScaleBaudRate: 9600,
            ScaleDataBits: 8,
            ScaleParity: 0,
            ScaleStopBits: 1,
            ScaleProtocol: ScaleProtocol.GenericAscii,
            ScaleTimeoutMs: 2000,
            IsScaleEnabled: true,
            UpdatedAtUtc: DateTime.UtcNow);

        coordinator.Initialize(config);

        Assert.True(coordinator.IsInitialized);
        Assert.Equal(terminalId, coordinator.TerminalId);
        Assert.NotNull(coordinator.ActiveConfig);

        // Test Print
        var printResult = await coordinator.PrintReceiptAsync([0x1B, 0x40]);
        Assert.True(printResult.Success);

        // Test Drawer Kick
        var drawerResult = await coordinator.OpenDrawerAsync("Manual open test");
        Assert.True(drawerResult.Success);

        // Test Scale Reading
        var scaleResult = await coordinator.ReadScaleWeightAsync();
        Assert.True(scaleResult.Success);
        Assert.Equal(1.250m, scaleResult.Weight);

        // Test Scale Zero & Tare
        var zeroResult = await coordinator.ZeroScaleAsync();
        Assert.True(zeroResult);

        var tareResult = await coordinator.TareScaleAsync();
        Assert.True(tareResult);
    }
}
