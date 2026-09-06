using Neuroma.Epub;

namespace Neuroma.Terminal;

internal enum ReaderColorMode { Plain, Gentle, Focus }

internal readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public RgbColor Dim(double factor) => new(
        (byte)Math.Clamp((int)Math.Round(Red * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(Green * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(Blue * factor), 0, 255));
}

internal static class TerminalTheme
{
    public static readonly RgbColor Header = new(132, 133, 129);
    public static readonly RgbColor Footer = new(101, 102, 99);
    private static readonly RgbColor Body = new(202, 200, 194);
    private static readonly RgbColor Dialogue = new(174, 202, 180);
    private static readonly RgbColor Emphasis = new(211, 188, 128);
    private static readonly RgbColor Strong = new(225, 221, 210);
    private static readonly RgbColor Code = new(196, 166, 205);
    private static readonly RgbColor Link = new(139, 184, 202);
    private static readonly RgbColor Quote = new(157, 181, 197);
    private static readonly RgbColor Heading = new(129, 181, 201);
    private static readonly RgbColor SecondaryHeading = new(112, 160, 177);
    private static readonly RgbColor List = new(194, 177, 130);
    private static readonly RgbColor Divider = new(103, 104, 101);

    public static RgbColor ColorFor(string line, EpubTextStyle style, bool dim)
    {
        RgbColor color;
        if (style.HasFlag(EpubTextStyle.DropCap)) color = Heading;
        else if (style.HasFlag(EpubTextStyle.Code)) color = Code;
        else if (style.HasFlag(EpubTextStyle.Emphasis)) color = Emphasis;
        else if (style.HasFlag(EpubTextStyle.Strong)) color = Strong;
        else if (style.HasFlag(EpubTextStyle.Link)) color = Link;
        else if (style.HasFlag(EpubTextStyle.Dialogue)) color = Dialogue;
        else if (style.HasFlag(EpubTextStyle.Blockquote)) color = Quote;
        else if (line.StartsWith("# ", StringComparison.Ordinal)) color = Heading;
        else if (line.StartsWith("##", StringComparison.Ordinal)) color = SecondaryHeading;
        else if (line.TrimStart().StartsWith("• ", StringComparison.Ordinal)) color = List;
        else if (line.StartsWith('│')) color = Quote;
        else if (line.StartsWith('─')) color = Divider;
        else color = Body;
        return dim ? color.Dim(0.58) : color;
    }
}
