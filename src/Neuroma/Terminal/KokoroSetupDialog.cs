using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Neuroma.Speech;

namespace Neuroma.Terminal;

internal sealed partial class KokoroSetupDialog
{
    private readonly TerminalScreen _screen;
    private readonly KokoroNarrator _narrator;
    private readonly ConcurrentQueue<(string Line, bool IsError)> _output = new();
    private readonly List<string> _log = [];
    private Process? _installer;
    private SetupStatus _status = SetupStatus.Offline;
    private string _detail;
    private string _stage = "Endpoint check";
    private int _progress;
    private int _activity;
    private bool _exitHandled;

    public KokoroSetupDialog(TerminalScreen screen, KokoroNarrator narrator, string detail)
    {
        _screen = screen;
        _narrator = narrator;
        _detail = detail;
    }

    public bool Show()
    {
        bool dirty = true;
        DateTime nextActivity = DateTime.UtcNow;
        try
        {
            while (true)
            {
                dirty |= DrainOutput();
                if (_installer is { HasExited: true } && !_exitHandled)
                {
                    CompleteInstallation();
                    dirty = true;
                }
                if (_status == SetupStatus.Installing && DateTime.UtcNow >= nextActivity)
                {
                    _activity = (_activity + 1) % 8;
                    nextActivity = DateTime.UtcNow.AddMilliseconds(180);
                    dirty = true;
                }
                if (dirty)
                {
                    Draw();
                    dirty = false;
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(25);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (_status == SetupStatus.Installing)
                {
                    if (key.Key is ConsoleKey.Escape or ConsoleKey.Q)
                    {
                        _detail = "Installation is still running. Keep Neuroma open until it finishes.";
                        dirty = true;
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.Escape) return false;
                if (key.Key == ConsoleKey.Enter && _status == SetupStatus.Ready) return true;
                if (key.Key == ConsoleKey.I)
                {
                    StartInstallation();
                    dirty = true;
                    continue;
                }
                if (key.Key == ConsoleKey.R)
                {
                    _detail = $"Checking {_narrator.KokoroUrl}";
                    _stage = "Endpoint check";
                    Draw();
                    if (_narrator.IsEndpointReachableAsync().GetAwaiter().GetResult()) return true;
                    _status = SetupStatus.Offline;
                    _detail = $"Still no response from {_narrator.KokoroUrl}";
                    dirty = true;
                }
            }
        }
        finally
        {
            if (_installer is { HasExited: true }) _installer.Dispose();
        }
    }

    private void StartInstallation()
    {
        if (!SupportsAutomaticInstallation())
        {
            _status = SetupStatus.Error;
            _stage = "Setup unavailable";
            _detail = OperatingSystem.IsMacOS()
                ? "Automatic local Kokoro installation requires an Apple Silicon Mac. Configure a reachable Kokoro URL on Intel Macs."
                : "Automatic local Kokoro installation requires Windows or an Apple Silicon Mac. Configure a reachable Kokoro URL on this platform.";
            return;
        }

        string installerName = OperatingSystem.IsWindows()
            ? "Install-Neuroma-Kokoro.ps1"
            : "Install-Neuroma-Kokoro.sh";
        string installerPath = Path.Combine(AppContext.BaseDirectory, installerName);
        if (!File.Exists(installerPath))
        {
            _status = SetupStatus.Error;
            _stage = "Setup failed";
            _detail = $"The installer is missing beside Neuroma.exe: {installerPath}";
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            IEnumerable<string> arguments = OperatingSystem.IsWindows()
                ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", installerPath, "-AppDirectory", AppContext.BaseDirectory]
                : [installerPath, AppContext.BaseDirectory];
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            _installer = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _installer.OutputDataReceived += (_, eventArgs) => QueueOutput(eventArgs.Data, false);
            _installer.ErrorDataReceived += (_, eventArgs) => QueueOutput(eventArgs.Data, true);
            if (!_installer.Start()) throw new InvalidOperationException("PowerShell did not start.");
            _installer.BeginOutputReadLine();
            _installer.BeginErrorReadLine();
            _status = SetupStatus.Installing;
            _detail = "Installing under LocalAppData. The first download may take several minutes.";
            _stage = "Preparing local installation";
            _progress = 1;
            _activity = 0;
            _exitHandled = false;
            _log.Clear();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _installer?.Dispose();
            _installer = null;
            _status = SetupStatus.Error;
            _stage = "Setup failed";
            _detail = ex.Message;
        }
    }

    private void CompleteInstallation()
    {
        if (_installer is null) return;
        _installer.WaitForExit();
        DrainOutput();
        _exitHandled = true;
        if (_installer.ExitCode != 0)
        {
            _status = SetupStatus.Error;
            _stage = "Setup failed";
            _detail = _log.Count > 0
                ? string.Join(" · ", _log.TakeLast(3))
                : $"The setup process exited with code {_installer.ExitCode}.";
            return;
        }

        _narrator.UseLocalEndpoint();
        if (!_narrator.IsEndpointReachableAsync(5000).GetAwaiter().GetResult())
        {
            _status = SetupStatus.Error;
            _stage = "Setup failed";
            _detail = "Local Kokoro installed, but its endpoint is not ready.";
            return;
        }
        _status = SetupStatus.Ready;
        _progress = 100;
        _stage = "Local service ready";
        _detail = "Local Kokoro is ready on 127.0.0.1:8880. Press Enter to read.";
    }

    private void QueueOutput(string? line, bool isError)
    {
        if (!string.IsNullOrWhiteSpace(line)) _output.Enqueue((line, isError));
    }

    private bool DrainOutput()
    {
        bool changed = false;
        while (_output.TryDequeue(out (string Line, bool IsError) item))
        {
            AcceptOutputLine(item.Line, item.IsError);
            changed = true;
        }
        return changed;
    }

    private void AcceptOutputLine(string rawLine, bool isError)
    {
        string line = CleanControlCharacters().Replace(rawLine, "").Trim();
        if (line.Length == 0) return;
        if (TryParseProgress(line, out int progress, out string stage))
        {
            _progress = progress;
            _stage = stage;
            _detail = stage;
            return;
        }
        if (line.StartsWith("NEUROMA_SETUP:", StringComparison.Ordinal)) return;
        string display = isError ? $"! {line}" : line;
        if (_log.LastOrDefault() != display) _log.Add(display);
        if (_log.Count > 4) _log.RemoveRange(0, _log.Count - 4);
    }

    internal static bool TryParseProgress(string line, out int progress, out string stage)
    {
        Match match = ProgressLine().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out progress))
        {
            progress = 0;
            stage = "";
            return false;
        }
        progress = Math.Clamp(progress, 0, 100);
        stage = string.IsNullOrWhiteSpace(match.Groups[2].Value) ? "Working" : match.Groups[2].Value.Trim();
        return true;
    }

    private void Draw()
    {
        _screen.WriteRgbRow(0, " ─ Kokoro voice setup", TerminalTheme.Header);
        for (int row = 0; row < Math.Max(1, _screen.Height - 2); row++)
            _screen.WriteRow(row + 1, "", ConsoleColor.Gray);

        int width = Math.Max(18, Math.Min(72, _screen.Width - 2));
        List<(string Text, ConsoleColor Color)> lines = BuildPanelLines(width);
        int firstRow = Math.Max(1, 1 + (Math.Max(1, _screen.Height - 2) - lines.Count) / 2);
        int left = Math.Max(0, (_screen.Width - width) / 2);
        for (int index = 0; index < lines.Count && firstRow + index < _screen.Height - 1; index++)
            _screen.WriteRow(firstRow + index, new string(' ', left) + lines[index].Text, lines[index].Color);

        string footer = _status == SetupStatus.Installing
            ? " ─ Installing · keep this window open"
            : " ─ I install/start local · R retry endpoint · Esc close";
        _screen.WriteRgbRow(_screen.Height - 1, footer, TerminalTheme.Footer);
    }

    private List<(string Text, ConsoleColor Color)> BuildPanelLines(int width)
    {
        int inner = Math.Max(14, width - 2);
        string status = _status switch
        {
            SetupStatus.Installing => "INSTALLING LOCAL KOKORO",
            SetupStatus.Ready => "LOCAL KOKORO READY",
            SetupStatus.Error => "LOCAL SETUP FAILED",
            _ => "KOKORO ENDPOINT OFFLINE"
        };
        ConsoleColor statusColor = _status == SetupStatus.Error ? ConsoleColor.Red : ConsoleColor.Cyan;
        var lines = new List<(string, ConsoleColor)>
        {
            ($"╭{new string('─', inner)}╮", ConsoleColor.DarkGray),
            (PanelRow($"  {status}", inner), statusColor),
            (PanelRow("", inner), ConsoleColor.Gray)
        };
        foreach (string detailLine in WrapWords(_detail, Math.Max(10, inner - 4)))
            lines.Add((PanelRow($"  {detailLine}", inner), ConsoleColor.Gray));

        if (_status == SetupStatus.Installing)
        {
            lines.Add((PanelRow($"  {ProgressText(Math.Max(8, inner - 16))}", inner), ConsoleColor.Cyan));
            foreach (string logLine in _log.TakeLast(3))
                lines.Add((PanelRow($"  › {logLine}", inner), ConsoleColor.DarkGray));
            lines.Add((PanelRow("  No administrator access is used.", inner), ConsoleColor.DarkGray));
        }
        else if (_status == SetupStatus.Ready)
        {
            lines.Add((PanelRow("", inner), ConsoleColor.Gray));
            lines.Add((PanelRow("  [ ENTER ]  START READING", inner), ConsoleColor.Cyan));
            lines.Add((PanelRow("  [ ESC   ]  CLOSE", inner), ConsoleColor.DarkGray));
        }
        else
        {
            lines.Add((PanelRow("", inner), ConsoleColor.Gray));
            if (SupportsAutomaticInstallation())
                lines.Add((PanelRow("  [ I ]  INSTALL / START LOCAL KOKORO", inner), ConsoleColor.Cyan));
            else
                lines.Add((PanelRow(OperatingSystem.IsMacOS()
                    ? "  Local setup requires an Apple Silicon build."
                    : "  Automatic local setup is unavailable here.", inner), ConsoleColor.DarkYellow));
            lines.Add((PanelRow("  [ R ]  RETRY CONFIGURED ENDPOINT", inner), ConsoleColor.Gray));
            lines.Add((PanelRow("  [ ESC ]  CONTINUE WITHOUT SPEECH", inner), ConsoleColor.DarkGray));
            if (SupportsAutomaticInstallation())
            {
                lines.Add((PanelRow("", inner), ConsoleColor.Gray));
                lines.Add((PanelRow("  Downloads: uv, Python, Kokoro and model data.", inner), ConsoleColor.DarkGray));
                lines.Add((PanelRow("  Source: github.com/remsky/Kokoro-FastAPI", inner), ConsoleColor.DarkGray));
            }
        }
        lines.Add(($"╰{new string('─', inner)}╯", ConsoleColor.DarkGray));
        return lines;
    }

    private string ProgressText(int barWidth)
    {
        int filled = (int)Math.Floor(Math.Clamp(_progress, 0, 100) / 100d * barWidth);
        char[] cells = Enumerable.Range(0, barWidth).Select(index => index < filled ? '━' : '·').ToArray();
        if (_progress < 100 && filled < barWidth)
            cells[filled + _activity % Math.Max(1, barWidth - filled)] = '◆';
        return $"[{new string(cells)}] {_progress,3}%";
    }

    private static string PanelRow(string text, int inner)
        => $"│{TextWrapper.Fit(text, inner)}│";

    private static IReadOnlyList<string> WrapWords(string text, int width)
    {
        var lines = new List<string>();
        string current = "";
        foreach (string word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length == 0) current = word;
            else if (current.Length + word.Length + 1 <= width) current += " " + word;
            else { lines.Add(current); current = word; }
        }
        if (current.Length > 0) lines.Add(current);
        return lines.Count > 0 ? lines : [""];
    }

    private enum SetupStatus { Offline, Installing, Ready, Error }

    private static bool SupportsAutomaticInstallation()
        => OperatingSystem.IsWindows() ||
           (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64);

    [GeneratedRegex(@"^NEUROMA_PROGRESS\|(\d{1,3})\|(.*)$")]
    private static partial Regex ProgressLine();

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|[\x00-\x08\x0b\x0c\x0e-\x1f]")]
    private static partial Regex CleanControlCharacters();
}
