using System.Text;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Hardware;

public sealed class EscPosBuilder
{
    private readonly List<byte> _buffer = [];
    private readonly StringBuilder _preview = new();
    private readonly PrinterEncoding _encoding;
    private readonly int _codePage;

    public EscPosBuilder(PrinterEncoding encoding = PrinterEncoding.Ascii, int codePage = 0)
    {
        _encoding = encoding;
        _codePage = codePage;
    }

    public EscPosBuilder Initialize()
    {
        _buffer.AddRange(EscPosCommands.Initialize);
        if (_codePage > 0)
        {
            _buffer.AddRange(EscPosCommands.CodePage((byte)_codePage));
        }
        return this;
    }

    public EscPosBuilder AlignLeft()
    {
        _buffer.AddRange(EscPosCommands.AlignLeft);
        return this;
    }

    public EscPosBuilder AlignCenter()
    {
        _buffer.AddRange(EscPosCommands.AlignCenter);
        return this;
    }

    public EscPosBuilder AlignRight()
    {
        _buffer.AddRange(EscPosCommands.AlignRight);
        return this;
    }

    public EscPosBuilder Bold(bool on = true)
    {
        _buffer.AddRange(on ? EscPosCommands.BoldOn : EscPosCommands.BoldOff);
        return this;
    }

    public EscPosBuilder TextNormal()
    {
        _buffer.AddRange(EscPosCommands.TextNormal);
        return this;
    }

    public EscPosBuilder TextDoubleHeight()
    {
        _buffer.AddRange(EscPosCommands.TextDoubleHeight);
        return this;
    }

    public EscPosBuilder TextDoubleWidth()
    {
        _buffer.AddRange(EscPosCommands.TextDoubleWidth);
        return this;
    }

    public EscPosBuilder TextDoubleBoth()
    {
        _buffer.AddRange(EscPosCommands.TextDoubleBoth);
        return this;
    }

    public EscPosBuilder WriteText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return this;

        var bytes = ReceiptEncodingStrategy.Encode(text, _encoding, _codePage);
        _buffer.AddRange(bytes);
        _preview.Append(ReceiptEncodingStrategy.TransliterateForPreview(text));
        return this;
    }

    public EscPosBuilder WriteLine(string text = "")
    {
        if (!string.IsNullOrEmpty(text))
        {
            WriteText(text);
        }
        _buffer.Add(EscPosCommands.LF);
        _preview.AppendLine();
        return this;
    }

    public EscPosBuilder FeedLines(byte count = 3)
    {
        _buffer.AddRange(EscPosCommands.FeedLines(count));
        for (int i = 0; i < count; i++)
            _preview.AppendLine();
        return this;
    }

    public EscPosBuilder Cut(bool partial = true)
    {
        _buffer.AddRange(partial ? EscPosCommands.PartialCut : EscPosCommands.FullCut);
        _preview.AppendLine("[=== CUT ===]");
        return this;
    }

    public EscPosBuilder KickDrawer(int pin = 0)
    {
        _buffer.AddRange(pin == 1 ? EscPosCommands.DrawerKickPin5 : EscPosCommands.DrawerKickPin2);
        _preview.AppendLine("[=== DRAWER KICK ===]");
        return this;
    }

    public byte[] ToBytes() => _buffer.ToArray();

    public string ToAsciiPreview() => _preview.ToString();
}
