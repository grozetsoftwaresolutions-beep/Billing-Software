using RetailPOS.Application.Hardware;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Hardware;

namespace RetailPOS.E2E.Tests.Fixtures;

/// <summary>
/// Manages controllable mock hardware devices for end-to-end integration and failure-injection tests.
/// Provides deterministic hooks to simulate hardware faults (offline, out-of-paper, jammed, disconnected).
/// </summary>
public sealed class MockHardwareDeviceManager
{
    public SimulatedReceiptPrinter Printer { get; }
    public SimulatedCashDrawer Drawer { get; }
    public SimulatedWeighingScale Scale { get; }

    public MockHardwareDeviceManager()
    {
        Printer = new SimulatedReceiptPrinter();
        Drawer = new SimulatedCashDrawer();
        Scale = new SimulatedWeighingScale();
    }

    public MockHardwareDeviceManager(
        SimulatedReceiptPrinter printer,
        SimulatedCashDrawer drawer,
        SimulatedWeighingScale scale)
    {
        Printer = printer ?? throw new ArgumentNullException(nameof(printer));
        Drawer = drawer ?? throw new ArgumentNullException(nameof(drawer));
        Scale = scale ?? throw new ArgumentNullException(nameof(scale));
    }

    public void SetPrinterOffline(bool offline)
    {
        Printer.SimulateOffline = offline;
    }

    public void SetPrinterOutOfPaper(bool outOfPaper)
    {
        Printer.SimulateOutOfPaper = outOfPaper;
    }

    public void SetDrawerJammed(bool jammed)
    {
        Drawer.SimulateJammed = jammed;
    }

    public void SetScaleDisconnected(bool disconnected)
    {
        Scale.SimulateDisconnected = disconnected;
    }

    public void SetScaleReading(decimal weight, ScaleWeightUnit unit = ScaleWeightUnit.Kilogram, bool stable = true)
    {
        Scale.CurrentWeight = weight;
        Scale.Unit = unit;
        Scale.IsStable = stable;
    }

    public void ResetAll()
    {
        Printer.Clear();
        Printer.SimulateOffline = false;
        Printer.SimulateOutOfPaper = false;

        Drawer.Reset();

        Scale.CurrentWeight = 1.250m;
        Scale.Unit = ScaleWeightUnit.Kilogram;
        Scale.IsStable = true;
        Scale.IsTare = false;
        Scale.SimulateDisconnected = false;
    }
}
