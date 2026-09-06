using Neuroma.Epub;

namespace Neuroma.Terminal;

internal static class TerminalDropCapRenderer
{
    private static readonly IReadOnlyDictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
    {
        ['A'] = [".###.", "#...#", "#####", "#...#"], ['B'] = ["####.", "#...#", "####.", "####."],
        ['C'] = [".####", "#....", "#....", ".####"], ['D'] = ["####.", "#...#", "#...#", "####."],
        ['E'] = ["#####", "#....", "####.", "#####"], ['F'] = ["#####", "#....", "####.", "#...."],
        ['G'] = [".####", "#....", "#..##", ".###."], ['H'] = ["#...#", "#...#", "#####", "#...#"],
        ['I'] = ["#####", "..#..", "..#..", "#####"], ['J'] = ["....#", "....#", "#...#", ".###."],
        ['K'] = ["#..#.", "###..", "#..#.", "#...#"], ['L'] = ["#....", "#....", "#....", "#####"],
        ['M'] = ["#...#", "#####", "#.#.#", "#...#"], ['N'] = ["#...#", "##..#", "#.#.#", "#..##"],
        ['O'] = [".###.", "#...#", "#...#", ".###."], ['P'] = ["####.", "#...#", "####.", "#...."],
        ['Q'] = [".###.", "#...#", "#..##", ".####"], ['R'] = ["####.", "#...#", "####.", "#..#."],
        ['S'] = [".####", "#....", ".###.", "####."], ['T'] = ["#####", "..#..", "..#..", "..#.."],
        ['U'] = ["#...#", "#...#", "#...#", ".###."], ['V'] = ["#...#", "#...#", ".#.#.", "..#.."],
        ['W'] = ["#...#", "#.#.#", "#####", "#...#"], ['X'] = ["#...#", ".#.#.", ".#.#.", "#...#"],
        ['Y'] = ["#...#", ".#.#.", "..#..", "..#.."], ['Z'] = ["#####", "...#.", ".#...", "#####"]
    };

    public static IReadOnlyList<DisplayLine> Render(EpubDropCap dropCap, int width, int paragraphId = -1)
    {
        string[] glyph = MakeGlyph(dropCap.Initial);
        int leftWidth = dropCap.Prefix.Length + glyph[0].Length + 1;
        int sideWidth = Math.Max(8, width - leftWidth);
        IReadOnlyList<string> narrowLines = TextWrapper.Wrap([dropCap.Remainder], sideWidth);
        string first = narrowLines.Count > 0 ? narrowLines[0] : "";
        string second = narrowLines.Count > 1 ? narrowLines[1] : "";
        int firstStart = FindFrom(dropCap.Remainder, first, 0);
        int secondStart = FindFrom(dropCap.Remainder, second, firstStart + first.Length);
        EpubTextStyle[] sourceStyles = NormalizeStyles(dropCap.Styles, dropCap.Text.Length);
        var result = new List<DisplayLine>
        {
            CreateFirstLine(dropCap, glyph[0], first, firstStart, sourceStyles, paragraphId),
            new(new string(' ', dropCap.Prefix.Length) + glyph[1] + " " + second,
                SpokenText: second, SpokenColumnMap: LinearMap(second.Length, leftWidth),
                Styles: CreateSecondLineStyles(dropCap, glyph[1], second, secondStart, sourceStyles), ParagraphId: paragraphId)
        };

        if (narrowLines.Count > 2)
        {
            int remainingStart = FindFrom(dropCap.Remainder, narrowLines[2], secondStart + second.Length);
            string remaining = dropCap.Remainder[remainingStart..];
            int styleStart = dropCap.Prefix.Length + 1 + remainingStart;
            var styledRemainder = new EpubText(remaining, sourceStyles[styleStart..]);
            result.AddRange(TextWrapper.WrapStyled(styledRemainder, width, paragraphId)
                .Select(line => new DisplayLine(line.Text, Styles: line.Styles, ParagraphId: paragraphId)));
        }
        return result;
    }

    private static DisplayLine CreateFirstLine(EpubDropCap dropCap, string glyph, string remainder, int remainderStart,
        IReadOnlyList<EpubTextStyle> sourceStyles, int paragraphId)
    {
        string content = dropCap.Prefix + glyph + " " + remainder;
        string spoken = dropCap.Prefix + dropCap.Initial + remainder;
        int prefixLength = dropCap.Prefix.Length;
        int replacementWidth = glyph.Length + 1;
        var map = new int[spoken.Length + 1];
        for (int index = 0; index <= prefixLength; index++) map[index] = index;
        map[prefixLength + 1] = prefixLength + replacementWidth;
        for (int index = 1; index <= remainder.Length; index++)
            map[prefixLength + 1 + index] = prefixLength + replacementWidth + index;
        var styles = new EpubTextStyle[content.Length];
        for (int index = 0; index < prefixLength; index++) styles[index] = sourceStyles[index];
        EpubTextStyle initialStyle = sourceStyles[prefixLength] | EpubTextStyle.DropCap;
        for (int index = prefixLength; index < prefixLength + glyph.Length; index++) styles[index] = initialStyle;
        int sourceStart = prefixLength + 1 + remainderStart;
        int displayStart = prefixLength + replacementWidth;
        for (int index = 0; index < remainder.Length; index++) styles[displayStart + index] = sourceStyles[sourceStart + index];
        return new DisplayLine(content, SpokenText: spoken, SpokenColumnMap: map, Styles: styles, ParagraphId: paragraphId);
    }

    private static IReadOnlyList<EpubTextStyle> CreateSecondLineStyles(EpubDropCap dropCap, string glyph,
        string text, int remainderStart, IReadOnlyList<EpubTextStyle> sourceStyles)
    {
        int prefixLength = dropCap.Prefix.Length;
        var styles = new EpubTextStyle[prefixLength + glyph.Length + 1 + text.Length];
        EpubTextStyle initialStyle = sourceStyles[prefixLength] | EpubTextStyle.DropCap;
        for (int index = prefixLength; index < prefixLength + glyph.Length; index++) styles[index] = initialStyle;
        int sourceStart = prefixLength + 1 + remainderStart;
        int displayStart = prefixLength + glyph.Length + 1;
        for (int index = 0; index < text.Length; index++) styles[displayStart + index] = sourceStyles[sourceStart + index];
        return styles;
    }

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

    private static IReadOnlyList<int> LinearMap(int textLength, int visibleStart)
        => Enumerable.Range(0, textLength + 1).Select(index => visibleStart + index).ToArray();

    private static string[] MakeGlyph(char value)
    {
        if (!Glyphs.TryGetValue(char.ToUpperInvariant(value), out string[]? pixels))
            return [$"[{value}]", "   "];
        return [Pack(pixels[0], pixels[1]), Pack(pixels[2], pixels[3])];
    }

    private static string Pack(string upper, string lower)
    {
        var cells = new char[upper.Length];
        for (int index = 0; index < cells.Length; index++)
            cells[index] = (upper[index] == '#', lower[index] == '#') switch
            {
                (true, true) => '█', (true, false) => '▀', (false, true) => '▄', _ => ' '
            };
        return new string(cells);
    }
}
