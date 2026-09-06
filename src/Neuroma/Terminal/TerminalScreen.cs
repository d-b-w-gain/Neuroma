using System.Runtime.InteropServices;

namespace Neuroma.Terminal;

internal sealed class TerminalScreen : IDisposable
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private bool _active;
    public int Width => Math.Max(20, Console.WindowWidth);
    public int Height => Math.Max(6, Console.WindowHeight);

    public void Enter()
    {
        if (_active) return; EnableAnsiOnWindows(); Console.TreatControlCAsInput = true;
        Console.Write("\x1b[?1049h\x1b[?25l"); Console.Clear(); _active = true;
    }
    public void WriteRow(int row, string text, ConsoleColor foreground, ConsoleColor background = ConsoleColor.Black)
    {
        if (row < 0 || row >= Height) return;
        Console.SetCursorPosition(0, row); Console.ForegroundColor = foreground; Console.BackgroundColor = background;
        Console.Write(TextWrapper.Fit(text, Width)); Console.ResetColor();
    }

    public void WriteImageRow(int row, int left, string ansiContent)
    {
        if (row < 0 || row >= Height) return;
        WriteRow(row, "", ConsoleColor.Gray);
        Console.SetCursorPosition(Math.Clamp(left, 0, Width - 1), row);
        Console.Write(ansiContent);
        Console.Write("\x1b[0m");
    }

    public void WriteHighlightedRow(int row, string prefix, string content, int start, int end, ConsoleColor foreground)
    {
        WriteRow(row, prefix + content, foreground);
        start = Math.Clamp(start, 0, content.Length);
        end = Math.Clamp(end, start, content.Length);
        int column = prefix.Length + start;
        if (end <= start || column >= Width) return;
        string highlighted = content[start..end];
        Console.SetCursorPosition(column, row);
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.Gray;
        Console.Write(highlighted);
        Console.ResetColor();
    }

    public void WriteStyledRow(int row, string prefix, string content, IReadOnlyList<RgbColor> colors,
        int highlightStart = -1, int highlightEnd = -1)
    {
        if (row < 0 || row >= Height) return;
        WriteRow(row, "", ConsoleColor.Gray);
        Console.SetCursorPosition(0, row);
        WriteForeground(new RgbColor(202, 200, 194));
        Console.Write(prefix);
        RgbColor? activeColor = null;
        bool highlighted = false;
        for (int index = 0; index < content.Length && prefix.Length + index < Width; index++)
        {
            bool nextHighlight = index >= highlightStart && index < highlightEnd;
            RgbColor color = index < colors.Count ? colors[index] : new RgbColor(202, 200, 194);
            if (nextHighlight != highlighted || (!nextHighlight && color != activeColor))
            {
                highlighted = nextHighlight; activeColor = color;
                if (highlighted) Console.Write("\x1b[38;2;18;18;18m\x1b[48;2;205;202;193m");
                else { Console.Write("\x1b[49m"); WriteForeground(color); }
            }
            Console.Write(content[index]);
        }
        Console.Write("\x1b[0m");
    }
    public string? Prompt(string label)
    {
        int row = Height - 1; WriteRow(row, "", ConsoleColor.Gray);
        Console.SetCursorPosition(0, row); Console.ForegroundColor = ConsoleColor.Yellow; Console.BackgroundColor = ConsoleColor.Black;
        Console.CursorVisible = true; Console.Write(label); string? value = Console.ReadLine();
        Console.CursorVisible = false; Console.ResetColor(); return value;
    }

    public void ToggleMaximize()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint window = GetConsoleWindow();
        if (window == 0) return;
        ShowWindow(window, IsZoomed(window) ? 9 : 3);
    }
    public void Dispose()
    {
        if (!_active) return; Console.ResetColor(); Console.Write("\x1b[?25h\x1b[?1049l");
        Console.TreatControlCAsInput = false; _active = false;
    }
    private static void EnableAnsiOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; nint handle = GetStdHandle(StdOutputHandle);
        if (handle == -1 || !GetConsoleMode(handle, out uint mode)) return;
        SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }
    private static void WriteForeground(RgbColor color)
        => Console.Write($"\x1b[38;2;{color.Red};{color.Green};{color.Blue}m");
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint h, uint mode);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hWnd);
}
