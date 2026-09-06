using System.Globalization;
using System.Text;
using Neuroma.Epub;

namespace Neuroma.Terminal;

internal static class TextWrapper
{
    public static IReadOnlyList<StyledWrappedLine> WrapStyled(EpubText source, int width, int paragraphId)
    {
        width = Math.Max(8, width);
        if (source.Text.Length == 0) return [new StyledWrappedLine("", [], paragraphId)];
        string continuation = ContinuationIndent(source.Text);
        string remaining = source.Text;
        EpubTextStyle[] styles = NormalizeStyles(source.Styles, source.Text.Length);
        var result = new List<StyledWrappedLine>();
        while (DisplayWidth(remaining) > width)
        {
            int cut = FindCut(remaining, width);
            int visibleLength = remaining[..cut].TrimEnd().Length;
            result.Add(new StyledWrappedLine(remaining[..visibleLength], styles[..visibleLength], paragraphId));
            int next = cut;
            while (next < remaining.Length && char.IsWhiteSpace(remaining[next])) next++;
            remaining = continuation + remaining[next..];
            styles = Enumerable.Repeat(EpubTextStyle.Normal, continuation.Length).Concat(styles[next..]).ToArray();
        }
        result.Add(new StyledWrappedLine(remaining, styles, paragraphId));
        return result;
    }

    public static IReadOnlyList<string> Wrap(IReadOnlyList<string> source, int width)
    {
        width = Math.Max(8, width); var result = new List<string>();
        foreach (string line in source)
        {
            if (line.Length == 0) { result.Add(""); continue; }
            string continuation = ContinuationIndent(line); string remaining = line;
            while (DisplayWidth(remaining) > width)
            {
                int cut = FindCut(remaining, width); result.Add(remaining[..cut].TrimEnd());
                remaining = continuation + remaining[cut..].TrimStart();
            }
            result.Add(remaining);
        }
        return result;
    }

    public static string Fit(string text, int width)
    {
        if (width <= 0) return "";
        if (DisplayWidth(text) > width) { int cut = FindCut(text, Math.Max(1, width - 1)); text = text[..cut].TrimEnd() + "…"; }
        return text + new string(' ', Math.Max(0, width - DisplayWidth(text)));
    }

    private static int FindCut(string text, int maxWidth)
    {
        int width = 0, lastSpace = -1, index = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int runeWidth = RuneWidth(rune); if (width + runeWidth > maxWidth) break;
            if (Rune.IsWhiteSpace(rune)) lastSpace = index;
            width += runeWidth; index += rune.Utf16SequenceLength;
        }
        return lastSpace > 0 ? lastSpace : Math.Max(1, index);
    }

    private static int DisplayWidth(string text) => text.EnumerateRunes().Sum(RuneWidth);
    private static int RuneWidth(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int value = rune.Value;
        bool wide = value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or
                    >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or
                    >= 0xFE10 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or
                    >= 0x1F300 and <= 0x1FAFF;
        return wide ? 2 : 1;
    }
    private static string ContinuationIndent(string line)
    {
        if (line.StartsWith("│ ")) return "│ ";
        int bullet = line.IndexOf("• ", StringComparison.Ordinal);
        if (bullet >= 0 && bullet < 8) return new string(' ', bullet + 2);
        if (line.StartsWith('#')) return "  ";
        return "";
    }

    private static EpubTextStyle[] NormalizeStyles(IReadOnlyList<EpubTextStyle>? styles, int length)
    {
        var result = new EpubTextStyle[length];
        if (styles is null) return result;
        for (int index = 0; index < length && index < styles.Count; index++) result[index] = styles[index];
        return result;
    }
}

internal sealed record StyledWrappedLine(string Text, IReadOnlyList<EpubTextStyle> Styles, int ParagraphId);
