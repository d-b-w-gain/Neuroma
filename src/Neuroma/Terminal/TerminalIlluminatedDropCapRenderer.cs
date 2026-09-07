using System.Reflection;
using Neuroma.Epub;

namespace Neuroma.Terminal;

internal static class TerminalIlluminatedDropCapRenderer
{
    private static readonly HashSet<char> AvailableLetters = ['A', 'C', 'M', 'S'];
    private static readonly Dictionary<(char Letter, int Columns), TerminalImageBlock> Cache = [];
    private static readonly object CacheLock = new();

    public static bool TryRender(EpubDropCap dropCap, int width, int paragraphId,
        out IReadOnlyList<DisplayLine> lines)
    {
        char letter = char.ToUpperInvariant(dropCap.Initial);
        if (!AvailableLetters.Contains(letter) || width < 32)
        {
            lines = [];
            return false;
        }

        int requestedColumns = Math.Clamp(width / 5, 10, 16);
        TerminalImageBlock block;
        try { block = GetBlock(letter, requestedColumns); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException)
        {
            lines = [];
            return false;
        }

        int prefixLength = dropCap.Prefix.Length;
        int textColumn = prefixLength + block.Width + 2;
        int sideWidth = width - textColumn;
        if (sideWidth < 12 || block.Rows.Count == 0)
        {
            lines = [];
            return false;
        }

        EpubTextStyle[] sourceStyles = NormalizeStyles(dropCap.Styles, dropCap.Text.Length);
        IReadOnlyList<string> narrowLines = TextWrapper.Wrap([dropCap.Remainder], sideWidth);
        var result = new List<DisplayLine>();
        int sourceCursor = 0;

        for (int row = 0; row < block.Rows.Count; row++)
        {
            string beside = row < narrowLines.Count ? narrowLines[row] : "";
            int besideStart = FindFrom(dropCap.Remainder, beside, sourceCursor);
            sourceCursor = besideStart + beside.Length;
            string prefix = row == 0 ? dropCap.Prefix : new string(' ', prefixLength);
            string content = prefix + new string(' ', block.Width + 2) + beside;
            EpubTextStyle[] styles = CreateStyles(content.Length, sourceStyles, prefixLength,
                textColumn, besideStart, beside.Length, row == 0);

            if (row == 0)
            {
                string spoken = dropCap.Prefix + dropCap.Initial + beside;
                result.Add(new DisplayLine(content, SpokenText: spoken,
                    SpokenColumnMap: CreateFirstLineMap(prefixLength, block.Width, beside.Length),
                    Styles: styles, ParagraphId: paragraphId, AnsiOverlay: block.Rows[row],
                    AnsiOverlayColumn: prefixLength));
            }
            else
            {
                result.Add(new DisplayLine(content, SpokenText: beside,
                    SpokenColumnMap: LinearMap(beside.Length, textColumn), Styles: styles,
                    ParagraphId: paragraphId, AnsiOverlay: block.Rows[row],
                    AnsiOverlayColumn: prefixLength));
            }
        }

        if (narrowLines.Count > block.Rows.Count)
        {
            int remainingStart = FindFrom(dropCap.Remainder, narrowLines[block.Rows.Count], sourceCursor);
            string remaining = dropCap.Remainder[remainingStart..];
            int styleStart = prefixLength + 1 + remainingStart;
            var styledRemainder = new EpubText(remaining, sourceStyles[styleStart..]);
            result.AddRange(TextWrapper.WrapStyled(styledRemainder, width, paragraphId)
                .Select(line => new DisplayLine(line.Text, Styles: line.Styles, ParagraphId: paragraphId)));
        }

        lines = result;
        return true;
    }

    private static TerminalImageBlock GetBlock(char letter, int columns)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue((letter, columns), out TerminalImageBlock? cached)) return cached;
            string name = $"Neuroma.DropCaps.celtic-{char.ToLowerInvariant(letter)}.png";
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing illuminated initial resource {name}.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            TerminalImageBlock rendered = TerminalImageRenderer.RenderBlock(buffer.ToArray(), columns, 8);
            Cache[(letter, columns)] = rendered;
            return rendered;
        }
    }

    private static EpubTextStyle[] CreateStyles(int length, IReadOnlyList<EpubTextStyle> sourceStyles,
        int prefixLength, int textColumn, int besideStart, int besideLength, bool includePrefix)
    {
        var styles = new EpubTextStyle[length];
        if (includePrefix)
            for (int index = 0; index < prefixLength; index++) styles[index] = sourceStyles[index];
        EpubTextStyle initialStyle = sourceStyles[prefixLength] | EpubTextStyle.DropCap;
        for (int index = prefixLength; index < textColumn; index++) styles[index] = initialStyle;
        int sourceStart = prefixLength + 1 + besideStart;
        for (int index = 0; index < besideLength; index++) styles[textColumn + index] = sourceStyles[sourceStart + index];
        return styles;
    }

    private static IReadOnlyList<int> CreateFirstLineMap(int prefixLength, int imageWidth, int textLength)
    {
        int textColumn = prefixLength + imageWidth + 2;
        var map = new int[prefixLength + 1 + textLength + 1];
        for (int index = 0; index <= prefixLength; index++) map[index] = index;
        map[prefixLength + 1] = textColumn;
        for (int index = 1; index <= textLength; index++) map[prefixLength + 1 + index] = textColumn + index;
        return map;
    }

    private static IReadOnlyList<int> LinearMap(int textLength, int visibleStart)
        => Enumerable.Range(0, textLength + 1).Select(index => visibleStart + index).ToArray();

    private static int FindFrom(string source, string value, int start)
    {
        if (value.Length == 0) return Math.Clamp(start, 0, source.Length);
        int found = source.IndexOf(value, Math.Clamp(start, 0, source.Length), StringComparison.Ordinal);
        return found >= 0 ? found : Math.Clamp(start, 0, source.Length);
    }

    private static EpubTextStyle[] NormalizeStyles(IReadOnlyList<EpubTextStyle>? styles, int length)
    {
        var result = new EpubTextStyle[length];
        if (styles is null) return result;
        for (int index = 0; index < length && index < styles.Count; index++) result[index] = styles[index];
        return result;
    }
}
