namespace RetailPOS.Domain.Enums;

public enum PaperWidth
{
    Width80mm = 80,
    Width58mm = 58
}

public enum PrinterConnectionType
{
    Simulated = 0,
    WindowsSpooler = 1,
    NetworkTcp = 2,
    RawUsb = 3
}

public enum DrawerConnectionType
{
    Simulated = 0,
    PrinterDriven = 1,
    SerialUsb = 2,
    None = 3
}

public enum ScaleConnectionType
{
    Simulated = 0,
    SerialCom = 1,
    NetworkTcp = 2,
    None = 3
}

public enum ScaleProtocol
{
    GenericAscii = 0,
    CasToledo = 1,
    Simulated = 2
}

public enum PrintStatus
{
    Success = 1,
    Failed = 2,
    Skipped = 3
}

public enum BarcodeScanSource
{
    ScannerWedge = 1,
    ManualEntry = 2
}

public enum ScaleWeightUnit
{
    Kilogram = 1,
    Gram = 2,
    Pound = 3
}

public enum PrinterEncoding
{
    Ascii = 0,
    Windows1252 = 1,
    Utf8 = 2,
    Custom = 3
}
