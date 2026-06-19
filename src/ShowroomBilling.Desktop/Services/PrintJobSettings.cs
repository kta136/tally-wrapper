namespace ShowroomBilling.Desktop.Services;

public enum PrintDuplexMode
{
    PrinterDefault = 0,
    OneSided = 1,
    TwoSidedLongEdge = 2,
    TwoSidedShortEdge = 3,
}

public enum PrintColorMode
{
    PrinterDefault = 0,
    Color = 1,
    Monochrome = 2,
}

public enum PrintCollationMode
{
    PrinterDefault = 0,
    Collated = 1,
    Uncollated = 2,
}

public readonly record struct PrintJobSettings(
    PrintDuplexMode Duplex,
    PrintColorMode Color,
    PrintCollationMode Collation)
{
    public static PrintJobSettings Default { get; } = new(
        PrintDuplexMode.PrinterDefault,
        PrintColorMode.PrinterDefault,
        PrintCollationMode.PrinterDefault);

    public bool IsPrinterDefault =>
        Duplex == PrintDuplexMode.PrinterDefault
        && Color == PrintColorMode.PrinterDefault
        && Collation == PrintCollationMode.PrinterDefault;
}

public sealed record PrintJobCapabilities(
    bool IsKnown,
    IReadOnlyList<PrintDuplexMode> DuplexModes,
    IReadOnlyList<PrintColorMode> ColorModes,
    IReadOnlyList<PrintCollationMode> CollationModes)
{
    public static PrintJobCapabilities Unknown { get; } = new(
        IsKnown: false,
        DuplexModes: [PrintDuplexMode.PrinterDefault],
        ColorModes: [PrintColorMode.PrinterDefault],
        CollationModes: [PrintCollationMode.PrinterDefault]);
}
