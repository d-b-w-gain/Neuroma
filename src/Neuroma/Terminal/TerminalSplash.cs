namespace Neuroma.Terminal;

internal static class TerminalSplash
{
    private static readonly RgbColor Frame = new(92, 119, 120);
    private static readonly RgbColor Subtitle = new(174, 202, 180);
    private static readonly RgbColor Signal = new(211, 188, 128);
    private static readonly RgbColor Prompt = new(132, 133, 129);
    private static readonly RgbColor[] WordmarkColors =
    [
        new(128, 175, 190), new(135, 185, 194), new(145, 195, 190), new(158, 202, 180),
        new(176, 202, 167), new(194, 198, 150), new(211, 188, 128)
    ];

    private static readonly string[][] WordmarkLetters =
    [
        ["╷     ╷", "│╲    │", "│ ╲   │", "│  ╲  │", "│   ╲ │", "│    ╲│", "╵     ╵"],
        ["┌──────", "│      ", "│      ", "├────╴ ", "│      ", "│      ", "└──────"],
        ["╷     ╷", "│     │", "│     │", "│     │", "│     │", "│     │", "╰─────╯"],
        ["┌─────╮", "│     │", "│     │", "├─────╯", "│  ╲   ", "│   ╲  ", "╵    ╲ "],
        ["╭─────╮", "│     │", "│     │", "│     │", "│     │", "│     │", "╰─────╯"],
        ["╷     ╷", "│╲   ╱│", "│ ╲ ╱ │", "│  ╳  │", "│     │", "│     │", "╵     ╵"],
        ["╭─────╮", "│     │", "│     │", "├─────┤", "│     │", "│     │", "╵     ╵"]
    ];

    public static bool Show(TerminalScreen screen)
    {
        int lastWidth = -1, lastHeight = -1;
        while (true)
        {
            if (screen.Width != lastWidth || screen.Height != lastHeight)
            {
                lastWidth = screen.Width;
                lastHeight = screen.Height;
                Draw(screen);
            }

            if (!Console.KeyAvailable) { Thread.Sleep(25); continue; }
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
            {
                screen.Clear();
                return true;
            }
            if (key.Key is ConsoleKey.Q or ConsoleKey.Escape) return false;
            if (key.Key == ConsoleKey.F11)
            {
                screen.ToggleMaximize();
                lastWidth = -1;
                Thread.Sleep(100);
            }
        }
    }

    internal static IReadOnlyList<SplashLine> BuildLayout(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        IReadOnlyList<SplashLine> content = width >= 70 && height >= 19 ? FullContent() : CompactContent(width);
        if (content.Count > height) content = MinimalContent(width);
        int top = Math.Max(0, (height - content.Count) / 2);
        return content.Select((line, index) => line with
        {
            Row = top + index,
            Text = Center(line.Text, width)
        }).Where(line => line.Row < height).ToArray();
    }

    private static void Draw(TerminalScreen screen)
    {
        screen.Clear();
        foreach (SplashLine line in BuildLayout(screen.Width, screen.Height))
            screen.WriteRgbRow(line.Row, line.Text, line.Color);
    }

    private static IReadOnlyList<SplashLine> FullContent()
    {
        const int innerWidth = 65;
        var lines = new List<SplashLine>
        {
            new(0, FrameTop("N E U R O M A", innerWidth), Frame),
            new(0, "│" + Center("TERMINAL EPUB READER", innerWidth) + "│", Subtitle),
            new(0, "╰" + new string('─', innerWidth) + "╯", Frame),
            new(0, "", Frame)
        };
        for (int row = 0; row < 7; row++)
            lines.Add(new SplashLine(0, string.Join("  ", WordmarkLetters.Select(letter => letter[row])), WordmarkColors[row]));
        lines.Add(new SplashLine(0, "", Frame));
        lines.Add(new SplashLine(0, "◆  EPUB REFLOW  ──  KOKORO VOICE  ──  LOCAL PROGRESS  ◆", Subtitle));
        lines.Add(new SplashLine(0, $"╶────────────[ READ // LISTEN · v{VersionText()} ]────────────╴", Frame));
        lines.Add(new SplashLine(0, "", Frame));
        lines.Add(new SplashLine(0, "◁━━━━━━━━━━━━━━━━ EPUB SIGNAL ACQUIRED ━━━━━━━━━━━━━━━━▷", Signal));
        lines.Add(new SplashLine(0, "ENTER OPEN  ·  Q EXIT  ·  F11 FULLSCREEN", Prompt));
        return lines;
    }

    private static IReadOnlyList<SplashLine> CompactContent(int width)
    {
        int innerWidth = Math.Clamp(width - 4, 16, 42);
        return
        [
            new(0, FrameTop("N E U R O M A", innerWidth), Frame),
            new(0, "│" + Center("TERMINAL EPUB READER", innerWidth) + "│", Subtitle),
            new(0, "╰" + new string('─', innerWidth) + "╯", Frame),
            new(0, "", Frame),
            new(0, $"READ // LISTEN · v{VersionText()}", Subtitle),
            new(0, "EPUB SIGNAL ACQUIRED", Signal),
            new(0, "ENTER OPEN · Q EXIT · F11 FULLSCREEN", Prompt)
        ];
    }

    private static IReadOnlyList<SplashLine> MinimalContent(int width)
        => width >= 15
            ? [new(0, "N E U R O M A", Subtitle), new(0, "ENTER OPEN · Q EXIT", Prompt)]
            : [new(0, "NEUROMA", Subtitle), new(0, "ENTER", Prompt)];

    private static string FrameTop(string label, int innerWidth)
    {
        string middle = $" {label} ";
        int remaining = Math.Max(0, innerWidth - middle.Length);
        int left = remaining / 2;
        return "╭" + new string('─', left) + middle + new string('─', remaining - left) + "╮";
    }

    private static string Center(string text, int width)
    {
        if (text.Length >= width) return text[..width];
        int left = (width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', width - text.Length - left);
    }

    private static string VersionText()
    {
        Version version = typeof(TerminalSplash).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}

internal sealed record SplashLine(int Row, string Text, RgbColor Color);
