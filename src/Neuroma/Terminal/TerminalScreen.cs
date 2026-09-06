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
    public string? Prompt(string label)
    {
        int row = Height - 1; WriteRow(row, "", ConsoleColor.White, ConsoleColor.DarkBlue);
        Console.SetCursorPosition(0, row); Console.ForegroundColor = ConsoleColor.White; Console.BackgroundColor = ConsoleColor.DarkBlue;
        Console.CursorVisible = true; Console.Write(label); string? value = Console.ReadLine();
        Console.CursorVisible = false; Console.ResetColor(); return value;
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint h, uint mode);
}

