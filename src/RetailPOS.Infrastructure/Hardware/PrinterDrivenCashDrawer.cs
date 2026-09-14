using RetailPOS.Application.Hardware;
using RetailPOS.Domain.Hardware;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class PrinterDrivenCashDrawer : ICashDrawer
{
    private readonly IReceiptPrinter _printer;
    private readonly int _pin;

    public PrinterDrivenCashDrawer(IReceiptPrinter printer, int pin = 0)
    {
        _printer = printer;
        _pin = pin;
    }

    public async Task<DrawerKickResult> OpenDrawerAsync(string reason, CancellationToken cancellationToken = default)
    {
        var command = _pin == 1 ? EscPosCommands.DrawerKickPin5 : EscPosCommands.DrawerKickPin2;
        var result = await _printer.PrintRawBytesAsync(command, cancellationToken);
        if (!result.Success)
        {
            return DrawerKickResult.Fail("PrinterDrivenDrawer", $"Printer drawer kick failed: {result.ErrorMessage}");
        }
        return DrawerKickResult.Ok("PrinterDrivenDrawer");
    }
}
