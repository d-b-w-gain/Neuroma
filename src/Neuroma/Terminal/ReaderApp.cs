using Neuroma.Epub;
using Neuroma.Speech;
using Neuroma.Storage;
using SixLabors.ImageSharp;

namespace Neuroma.Terminal;

public sealed class ReaderApp
{
    private readonly EpubBook _book; private readonly ProgressStore _progressStore; private readonly TerminalScreen _screen = new();
    private readonly KokoroNarrator _narrator;
    private int _chapterIndex, _offset, _lastWidth; private IReadOnlyList<DisplayLine> _wrappedLines = []; private string? _searchQuery;
    private int _drawRequested = 1;
    private SpeechCue? _appliedCue;
    private int BodyHeight => Math.Max(1, _screen.Height - 2);

    public ReaderApp(EpubBook book, ProgressStore progressStore, SpeechSettings speechSettings)
    {
        _book = book; _progressStore = progressStore; _narrator = new KokoroNarrator(speechSettings);
        _narrator.Changed += RequestDraw;
        ReadingPosition saved = progressStore.Get(book.FilePath);
        _chapterIndex = Math.Clamp(saved.Chapter, 0, book.ChapterCount - 1); Rewrap();
        _offset = (int)Math.Round(saved.Fraction * Math.Max(0, _wrappedLines.Count - BodyHeight));
    }

    public void Run()
    {
        _screen.Enter();
        try
        {
            bool running = true;
            while (running)
            {
                if (_screen.Width != _lastWidth)
                {
                    if (_narrator.IsRunning) _narrator.Stop("KOKORO · STOPPED AFTER RESIZE");
                    Rewrap(); RequestDraw();
                }
                ApplyNarrationCue();
                ClampOffset();
                if (Interlocked.Exchange(ref _drawRequested, 0) != 0) Draw();
                if (Console.KeyAvailable)
                {
                    running = HandleKey(Console.ReadKey(intercept: true));
                    RequestDraw();
                }
                else Thread.Sleep(25);
            }
        }
        finally
        {
            _narrator.Stop();
            _narrator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SaveProgress(); _screen.Dispose();
        }
    }

    private void RequestDraw() => Interlocked.Exchange(ref _drawRequested, 1);

    private bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C) return false;
        switch (key.Key)
        {
            case ConsoleKey.Q: case ConsoleKey.Escape: return false;
            case ConsoleKey.DownArrow: Scroll(1); break;
            case ConsoleKey.UpArrow: Scroll(-1); break;
            case ConsoleKey.PageDown: case ConsoleKey.Spacebar: Scroll(Math.Max(1, BodyHeight - 2)); break;
            case ConsoleKey.PageUp: Scroll(-Math.Max(1, BodyHeight - 2)); break;
            case ConsoleKey.RightArrow: ChangeChapter(1); break;
            case ConsoleKey.LeftArrow: ChangeChapter(-1); break;
            case ConsoleKey.Home: _offset = 0; break;
            case ConsoleKey.End: _offset = Math.Max(0, _wrappedLines.Count - BodyHeight); break;
            case ConsoleKey.T: StopNarrationForModal(); ShowTableOfContents(); break;
            case ConsoleKey.F11: _screen.ToggleMaximize(); break;
            case ConsoleKey.S when key.KeyChar == 's': ToggleNarration(); break;
            case ConsoleKey.Oem2 when key.KeyChar == '/': StopNarrationForModal(); StartSearch(); break;
            case ConsoleKey.N: FindMatch(key.Modifiers.HasFlag(ConsoleModifiers.Shift)); break;
            case ConsoleKey.I: StopNarrationForModal(); ShowInfo(); break;
            case ConsoleKey.Oem2 when key.KeyChar == '?': StopNarrationForModal(); ShowHelp(); break;
            default:
                if (key.KeyChar is 'j' or 'J') Scroll(1);
                else if (key.KeyChar is 'k' or 'K') Scroll(-1);
                else if (key.KeyChar is 'h' or 'H') ChangeChapter(-1);
                else if (key.KeyChar is 'l' or 'L') ChangeChapter(1);
                else if (key.KeyChar == 'g') _offset = 0;
                else if (key.KeyChar == 'G') _offset = Math.Max(0, _wrappedLines.Count - BodyHeight);
                break;
        }
        return true;
    }

    private void Scroll(int amount)
    {
        int old = _offset; _offset += amount; ClampOffset();
        if (amount > 0 && old == _offset && _chapterIndex < _book.ChapterCount - 1) ChangeChapter(1);
        else if (amount < 0 && old == _offset && _offset == 0 && _chapterIndex > 0)
        { ChangeChapter(-1); _offset = Math.Max(0, _wrappedLines.Count - BodyHeight); }
    }
    private void ChangeChapter(int delta)
    {
        int next = Math.Clamp(_chapterIndex + delta, 0, _book.ChapterCount - 1); if (next == _chapterIndex) return;
        if (_narrator.IsRunning) _narrator.Stop("KOKORO · STOPPED AFTER NAVIGATION");
        SaveProgress(); _chapterIndex = next; _offset = 0; Rewrap();
    }

    private void StopNarrationForModal()
    {
        if (_narrator.IsRunning) _narrator.Stop("KOKORO · STOPPED");
    }

    private void ToggleNarration()
    {
        if (_narrator.IsRunning)
        {
            _narrator.Stop("KOKORO · STOPPED");
            return;
        }

        _appliedCue = null;
        _narrator.Start(BuildSpeechChunks());
    }

    private IReadOnlyList<SpeechChunk> BuildSpeechChunks()
    {
        const int maximumCharacters = 700;
        var chunks = new List<SpeechChunk>();
        var text = new System.Text.StringBuilder();
        var spans = new List<SpeechSpan>();
        int width = Math.Max(20, Math.Min(100, _screen.Width - 4));

        void Flush()
        {
            if (text.Length == 0) return;
            chunks.Add(new SpeechChunk(text.ToString(), spans.ToArray()));
            text.Clear(); spans.Clear();
        }

        for (int chapterIndex = _chapterIndex; chapterIndex < _book.ChapterCount; chapterIndex++)
        {
            IReadOnlyList<DisplayLine> lines = chapterIndex == _chapterIndex
                ? _wrappedLines
                : BuildDisplayLines(_book.GetChapter(chapterIndex), width, renderImages: true);
            int firstLine = chapterIndex == _chapterIndex ? _offset : 0;
            for (int lineIndex = firstLine; lineIndex < lines.Count; lineIndex++)
            {
                if (!TryGetSpokenText(lines[lineIndex], out string spoken, out int visibleStart)) continue;
                int separatorLength = text.Length == 0 ? 0 : 1;
                if (text.Length + separatorLength + spoken.Length > maximumCharacters) Flush();
                if (text.Length > 0) text.Append(' ');
                int textStart = text.Length;
                text.Append(spoken);
                spans.Add(new SpeechSpan(textStart, text.Length, chapterIndex, lineIndex, visibleStart));
            }
            Flush();
        }
        return chunks;
    }

    private static bool TryGetSpokenText(DisplayLine line, out string spoken, out int visibleStart)
    {
        spoken = ""; visibleStart = 0;
        if (line.IsImage || string.IsNullOrWhiteSpace(line.Content) || line.Content.StartsWith("[Image:", StringComparison.Ordinal))
            return false;

        string content = line.Content;
        int start = 0;
        while (start < content.Length && char.IsWhiteSpace(content[start])) start++;
        while (start < content.Length && content[start] is '#' or '•' or '│' or '─' or '>') start++;
        while (start < content.Length && char.IsWhiteSpace(content[start])) start++;
        int end = content.Length;
        while (end > start && char.IsWhiteSpace(content[end - 1])) end--;
        if (end <= start || !content[start..end].Any(char.IsLetterOrDigit)) return false;
        spoken = content[start..end]; visibleStart = start;
        return true;
    }

    private void ApplyNarrationCue()
    {
        SpeechCue? cue = _narrator.CurrentCue;
        if (Equals(cue, _appliedCue)) return;
        _appliedCue = cue;
        if (cue is null) return;

        if (cue.ChapterIndex != _chapterIndex)
        {
            SaveProgress();
            _chapterIndex = cue.ChapterIndex;
            _offset = 0;
            Rewrap();
        }
        if (cue.LineIndex < _offset || cue.LineIndex >= _offset + BodyHeight)
            _offset = Math.Clamp(cue.LineIndex - BodyHeight / 3, 0, Math.Max(0, _wrappedLines.Count - BodyHeight));
        RequestDraw();
    }
    private void Rewrap()
    {
        _lastWidth = _screen.Width; int width = Math.Max(20, Math.Min(100, _screen.Width - 4));
        _wrappedLines = BuildDisplayLines(_book.GetChapter(_chapterIndex), width, renderImages: true); ClampOffset();
    }

    private IReadOnlyList<DisplayLine> BuildDisplayLines(EpubChapter chapter, int width, bool renderImages)
    {
        var result = new List<DisplayLine>();
        foreach (EpubElement element in chapter.Elements)
        {
            if (element is EpubText text)
            {
                result.AddRange(TextWrapper.Wrap([text.Text], width).Select(line => new DisplayLine(line)));
                continue;
            }
            if (element is not EpubImage image) continue;

            try
            {
                byte[]? data = renderImages && image.EntryPath.Length > 0 ? _book.ReadResource(image.EntryPath) : null;
                if (data is null)
                {
                    result.Add(new DisplayLine($"[Image: {image.AltText}]"));
                    continue;
                }
                result.AddRange(TerminalImageRenderer.Render(data, width, Math.Max(1, BodyHeight - 1))
                    .Select(line => new DisplayLine(line, IsImage: true)));
            }
            catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or
                                       NotSupportedException or InvalidDataException or IOException or ArgumentException)
            {
                result.Add(new DisplayLine($"[Image: {image.AltText} — unsupported format]"));
            }
        }
        return result.Count == 0 ? [new DisplayLine("(This chapter contains no displayable content.)")] : result;
    }
    private void ClampOffset() => _offset = Math.Clamp(_offset, 0, Math.Max(0, _wrappedLines.Count - BodyHeight));

    private void Draw()
    {
        EpubChapter chapter = _book.GetChapter(_chapterIndex);
        _screen.WriteRow(0, $" ─ Neuroma  {_book.Metadata.Title}  •  {chapter.Title}  [{_chapterIndex + 1}/{_book.ChapterCount}]",
            ConsoleColor.Gray);
        int contentWidth = Math.Max(20, Math.Min(100, _screen.Width - 4)); int left = Math.Max(0, (_screen.Width - contentWidth) / 2);
        string margin = new(' ', left);
        for (int row = 0; row < BodyHeight; row++)
        {
            int index = _offset + row; DisplayLine line = index < _wrappedLines.Count ? _wrappedLines[index] : new DisplayLine("");
            if (line.IsImage) _screen.WriteImageRow(row + 1, left, line.Content);
            else if (_narrator.CurrentCue is SpeechCue cue && cue.ChapterIndex == _chapterIndex && cue.LineIndex == index)
                _screen.WriteHighlightedRow(row + 1, margin, line.Content, cue.ColumnStart, cue.ColumnEnd, ColorFor(line.Content));
            else _screen.WriteRow(row + 1, margin + line.Content, ColorFor(line.Content));
        }
        int max = Math.Max(1, _wrappedLines.Count - BodyHeight); double chapterProgress = Math.Clamp((double)_offset / max, 0, 1);
        double bookProgress = (_chapterIndex + chapterProgress) / _book.ChapterCount;
        string search = string.IsNullOrEmpty(_searchQuery) ? "" : $"  /{_searchQuery}";
        string speech = _narrator.IsRunning || !_narrator.Status.EndsWith("READY", StringComparison.Ordinal)
            ? $"  {_narrator.Status}" : "";
        _screen.WriteRow(_screen.Height - 1, $" ─ {bookProgress:P0}  ↑↓ scroll  ←→ chapter  t toc  / search  s speak  q quit{search}{speech}",
            ConsoleColor.DarkGray);
    }
    private static ConsoleColor ColorFor(string line)
    {
        if (line.StartsWith("# ")) return ConsoleColor.Cyan;
        if (line.StartsWith("##")) return ConsoleColor.DarkCyan;
        if (line.TrimStart().StartsWith("• ")) return ConsoleColor.DarkYellow;
        if (line.StartsWith('│')) return ConsoleColor.DarkCyan;
        if (line.StartsWith("    ")) return ConsoleColor.DarkYellow;
        if (line.StartsWith('─')) return ConsoleColor.DarkGray;
        return ConsoleColor.Gray;
    }

    private void StartSearch()
    {
        string? query = _screen.Prompt(" Search: "); if (string.IsNullOrWhiteSpace(query)) return;
        _searchQuery = query.Trim(); FindMatch(false, true);
    }
    private void FindMatch(bool reverse, bool includeCurrent = false)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery)) { StartSearch(); return; }
        int direction = reverse ? -1 : 1, chapter = _chapterIndex, start = includeCurrent ? _offset : _offset + direction;
        for (int visited = 0; visited < _book.ChapterCount; visited++)
        {
            IReadOnlyList<DisplayLine> lines = chapter == _chapterIndex ? _wrappedLines :
                BuildDisplayLines(_book.GetChapter(chapter), Math.Max(20, Math.Min(100, _screen.Width - 4)), renderImages: false);
            int found = FindInLines(lines, _searchQuery, start, reverse);
            if (found >= 0)
            {
                if (chapter != _chapterIndex) { _chapterIndex = chapter; Rewrap(); }
                _offset = Math.Clamp(found, 0, Math.Max(0, _wrappedLines.Count - BodyHeight)); return;
            }
            chapter = (chapter + direction + _book.ChapterCount) % _book.ChapterCount; start = reverse ? int.MaxValue : 0;
        }
        FlashMessage($"No match for '{_searchQuery}'.");
    }
    private static int FindInLines(IReadOnlyList<DisplayLine> lines, string query, int start, bool reverse)
    {
        if (reverse)
            for (int i = Math.Min(start, lines.Count - 1); i >= 0; i--)
            { if (lines[i].SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return i; }
        else
            for (int i = Math.Max(0, start); i < lines.Count; i++)
            { if (lines[i].SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return i; }
        return -1;
    }

    private void ShowTableOfContents()
    {
        IReadOnlyList<TocEntry> entries = _book.TableOfContents.Count > 0 ? _book.TableOfContents :
            Enumerable.Range(0, _book.ChapterCount).Select(i => new TocEntry(_book.GetChapter(i).Title, i, null)).ToList();
        int selected = Math.Max(0, entries.ToList().FindIndex(e => e.ChapterIndex == _chapterIndex));
        int top = Math.Max(0, selected - BodyHeight / 2);
        while (true)
        {
            _screen.WriteRow(0, " ─ Table of contents — ↑↓ select, Enter open, Esc close", ConsoleColor.Gray);
            for (int row = 0; row < BodyHeight; row++)
            {
                int index = top + row;
                if (index >= entries.Count) { _screen.WriteRow(row + 1, "", ConsoleColor.Gray); continue; }
                TocEntry entry = entries[index]; string indent = new(' ', Math.Min(12, entry.Depth * 2));
                string label = $"{(index == selected ? '›' : ' ')} {indent}{entry.Label}";
                _screen.WriteRow(row + 1, label, index == selected ? ConsoleColor.Cyan : ConsoleColor.Gray);
            }
            _screen.WriteRow(_screen.Height - 1, $" ─ {selected + 1}/{entries.Count}", ConsoleColor.DarkGray);
            ConsoleKeyInfo key = Console.ReadKey(true); if (key.Key is ConsoleKey.Escape or ConsoleKey.Q) return;
            if (key.Key == ConsoleKey.Enter) { _chapterIndex = entries[selected].ChapterIndex; _offset = 0; Rewrap(); return; }
            int delta = key.Key switch { ConsoleKey.DownArrow or ConsoleKey.J => 1, ConsoleKey.UpArrow or ConsoleKey.K => -1,
                ConsoleKey.PageDown => BodyHeight, ConsoleKey.PageUp => -BodyHeight, _ => 0 };
            selected = Math.Clamp(selected + delta, 0, entries.Count - 1);
            if (selected < top) top = selected; if (selected >= top + BodyHeight) top = selected - BodyHeight + 1;
        }
    }

    private void ShowHelp() => ShowOverlay("Help", [
        "j / ↓        Scroll down one line", "k / ↑        Scroll up one line", "Space / PgDn Next page",
        "PgUp          Previous page", "h / ←         Previous chapter", "l / →         Next chapter",
        "g / G         Chapter start / end", "t             Table of contents", "/             Search the whole book",
        "n / N         Next / previous result", "s             Read aloud / stop (Kokoro)",
        "i             Book information", "F11           Maximize / restore window", "q / Esc       Quit", "",
        "Speech config: neuroma.json beside Neuroma.exe", "Overrides: --kokoro-url, --voice, --speed", "", "Press any key to return."]);
    private void ShowInfo() => ShowOverlay("Book information", [
        $"Title:      {_book.Metadata.Title}", $"Author:     {Fallback(_book.Metadata.Creator)}",
        $"Language:   {Fallback(_book.Metadata.Language)}", $"Identifier: {Fallback(_book.Metadata.Identifier)}",
        $"Chapters:   {_book.ChapterCount}", $"File:       {_book.FilePath}", "", "Press any key to return."]);
    private void ShowOverlay(string title, IReadOnlyList<string> lines)
    {
        _screen.WriteRow(0, $" ─ {title}", ConsoleColor.Gray);
        for (int row = 0; row < BodyHeight; row++) _screen.WriteRow(row + 1, row < lines.Count ? $"  {lines[row]}" : "", ConsoleColor.Gray);
        _screen.WriteRow(_screen.Height - 1, " ─ Any key returns to the book", ConsoleColor.DarkGray); Console.ReadKey(true);
    }
    private void FlashMessage(string message)
    { _screen.WriteRow(_screen.Height - 1, $" ! {message}  Press any key.", ConsoleColor.Red); Console.ReadKey(true); }
    private void SaveProgress()
    {
        double fraction = _wrappedLines.Count <= BodyHeight ? 0 : (double)_offset / (_wrappedLines.Count - BodyHeight);
        _progressStore.Save(_book.FilePath, new(_chapterIndex, fraction, DateTimeOffset.UtcNow));
    }
    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
