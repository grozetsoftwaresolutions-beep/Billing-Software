namespace RetailPOS.Domain.Hardware;

public static class EscPosCommands
{
    public const byte ESC = 0x1B;
    public const byte GS = 0x1D;
    public const byte LF = 0x0A;
    public const byte CR = 0x0D;

    // Initialize printer
    public static readonly byte[] Initialize = [ESC, 0x40];

    // Text alignment
    public static readonly byte[] AlignLeft = [ESC, 0x61, 0x00];
    public static readonly byte[] AlignCenter = [ESC, 0x61, 0x01];
    public static readonly byte[] AlignRight = [ESC, 0x61, 0x02];

    // Bold / Emphasis
    public static readonly byte[] BoldOn = [ESC, 0x45, 0x01];
    public static readonly byte[] BoldOff = [ESC, 0x45, 0x00];

    // Underline
    public static readonly byte[] UnderlineOn = [ESC, 0x2D, 0x01];
    public static readonly byte[] UnderlineOff = [ESC, 0x2D, 0x00];

    // Sizing
    public static readonly byte[] TextNormal = [GS, 0x21, 0x00];
    public static readonly byte[] TextDoubleHeight = [GS, 0x21, 0x01];
    public static readonly byte[] TextDoubleWidth = [GS, 0x21, 0x10];
    public static readonly byte[] TextDoubleBoth = [GS, 0x21, 0x11];

    // Cutting
    public static readonly byte[] PartialCut = [GS, 0x56, 0x42, 0x00]; // Feed and partial cut
    public static readonly byte[] FullCut = [GS, 0x56, 0x00];

    // Cash drawer kick
    public static readonly byte[] DrawerKickPin2 = [ESC, 0x70, 0x00, 0x19, 0xFA]; // Pin 2, 50ms pulse
    public static readonly byte[] DrawerKickPin5 = [ESC, 0x70, 0x01, 0x19, 0xFA]; // Pin 5, 50ms pulse

    public static byte[] CodePage(byte n) => [ESC, 0x74, n];
    public static byte[] FeedLines(byte count) => [ESC, 0x64, count];
}
