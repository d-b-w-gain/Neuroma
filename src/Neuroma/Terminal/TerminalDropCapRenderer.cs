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

    public static IReadOnlyList<DisplayLine> Render(EpubDropCap dropCap, int width)
    {
        string[] glyph = MakeGlyph(dropCap.Initial);
        int leftWidth = dropCap.Prefix.Length + glyph[0].Length + 1;
        int sideWidth = Math.Max(8, width - leftWidth);
        IReadOnlyList<string> narrowLines = TextWrapper.Wrap([dropCap.Remainder], sideWidth);
        string first = narrowLines.Count > 0 ? narrowLines[0] : "";
        string second = narrowLines.Count > 1 ? narrowLines[1] : "";
        var result = new List<DisplayLine>
        {
            CreateFirstLine(dropCap, glyph[0], first),
            new(new string(' ', dropCap.Prefix.Length) + glyph[1] + " " + second,
                SpokenText: second, SpokenColumnMap: LinearMap(second.Length, leftWidth))
        };

        if (narrowLines.Count > 2)
        {
            string remaining = string.Join(' ', narrowLines.Skip(2));
            result.AddRange(TextWrapper.Wrap([remaining], width).Select(line => new DisplayLine(line)));
        }
        return result;
    }

    private static DisplayLine CreateFirstLine(EpubDropCap dropCap, string glyph, string remainder)
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
        return new DisplayLine(content, SpokenText: spoken, SpokenColumnMap: map);
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
