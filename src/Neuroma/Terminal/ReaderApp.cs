using Neuroma.Epub;
using Neuroma.Storage;

namespace Neuroma.Terminal;

public sealed class ReaderApp
{
    private readonly EpubBook _book; private readonly ProgressStore _progressStore; private readonly TerminalScreen _screen = new();
    private int _chapterIndex, _offset, _lastWidth; private IReadOnlyList<string> _wrappedLines = []; private string? _searchQuery;
    private int BodyHeight => Math.Max(1, _screen.Height - 2);

    public ReaderApp(EpubBook book, ProgressStore progressStore)
    {
        _book = book; _progressStore = progressStore; ReadingPosition saved = progressStore.Get(book.FilePath);
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
                if (_screen.Width != _lastWidth) Rewrap(); ClampOffset(); Draw();
                running = HandleKey(Console.ReadKey(intercept: true));
            }
        }
        finally { SaveProgress(); _screen.Dispose(); }
    }

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
            case ConsoleKey.T: ShowTableOfContents(); break;
            case ConsoleKey.Oem2 when key.KeyChar == '/': StartSearch(); break;
            case ConsoleKey.N: FindMatch(key.Modifiers.HasFlag(ConsoleModifiers.Shift)); break;
            case ConsoleKey.I: ShowInfo(); break;
            case ConsoleKey.Oem2 when key.KeyChar == '?': ShowHelp(); break;
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
        SaveProgress(); _chapterIndex = next; _offset = 0; Rewrap();
    }
    private void Rewrap()
    {
        _lastWidth = _screen.Width; int width = Math.Max(20, Math.Min(100, _screen.Width - 4));
        _wrappedLines = TextWrapper.Wrap(_book.GetChapter(_chapterIndex).Lines, width); ClampOffset();
    }
    private void ClampOffset() => _offset = Math.Clamp(_offset, 0, Math.Max(0, _wrappedLines.Count - BodyHeight));

    private void Draw()
    {
        EpubChapter chapter = _book.GetChapter(_chapterIndex);
        _screen.WriteRow(0, $" Neuroma  {_book.Metadata.Title}  •  {chapter.Title}  [{_chapterIndex + 1}/{_book.ChapterCount}]",
            ConsoleColor.White, ConsoleColor.DarkBlue);
        int contentWidth = Math.Max(20, Math.Min(100, _screen.Width - 4)); int left = Math.Max(0, (_screen.Width - contentWidth) / 2);
        string margin = new(' ', left);
        for (int row = 0; row < BodyHeight; row++)
        {
            int index = _offset + row; string line = index < _wrappedLines.Count ? _wrappedLines[index] : "";
            _screen.WriteRow(row + 1, margin + line, ColorFor(line));
        }
        int max = Math.Max(1, _wrappedLines.Count - BodyHeight); double chapterProgress = Math.Clamp((double)_offset / max, 0, 1);
        double bookProgress = (_chapterIndex + chapterProgress) / _book.ChapterCount;
        string search = string.IsNullOrEmpty(_searchQuery) ? "" : $"  /{_searchQuery}";
        _screen.WriteRow(_screen.Height - 1, $" {bookProgress:P0}  ↑↓ scroll  ←→ chapter  t toc  / search  ? help  q quit{search}",
            ConsoleColor.White, ConsoleColor.DarkBlue);
    }
    private static ConsoleColor ColorFor(string line)
    {
        if (line.StartsWith("# ")) return ConsoleColor.Cyan;
        if (line.StartsWith("##")) return ConsoleColor.Green;
        if (line.TrimStart().StartsWith("• ")) return ConsoleColor.Yellow;
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
            IReadOnlyList<string> lines = chapter == _chapterIndex ? _wrappedLines :
                TextWrapper.Wrap(_book.GetChapter(chapter).Lines, Math.Max(20, Math.Min(100, _screen.Width - 4)));
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
    private static int FindInLines(IReadOnlyList<string> lines, string query, int start, bool reverse)
    {
        if (reverse)
            for (int i = Math.Min(start, lines.Count - 1); i >= 0; i--)
            { if (lines[i].Contains(query, StringComparison.CurrentCultureIgnoreCase)) return i; }
        else
            for (int i = Math.Max(0, start); i < lines.Count; i++)
            { if (lines[i].Contains(query, StringComparison.CurrentCultureIgnoreCase)) return i; }
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
            _screen.WriteRow(0, " Table of contents — ↑↓ select, Enter open, Esc close", ConsoleColor.White, ConsoleColor.DarkBlue);
            for (int row = 0; row < BodyHeight; row++)
            {
                int index = top + row;
                if (index >= entries.Count) { _screen.WriteRow(row + 1, "", ConsoleColor.Gray); continue; }
                TocEntry entry = entries[index]; string indent = new(' ', Math.Min(12, entry.Depth * 2));
                string label = $"{(index == selected ? '›' : ' ')} {indent}{entry.Label}";
                _screen.WriteRow(row + 1, label, index == selected ? ConsoleColor.Black : ConsoleColor.Gray,
                    index == selected ? ConsoleColor.Cyan : ConsoleColor.Black);
            }
            _screen.WriteRow(_screen.Height - 1, $" {selected + 1}/{entries.Count}", ConsoleColor.White, ConsoleColor.DarkBlue);
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
        "n / N         Next / previous result", "i             Book information", "q / Esc       Quit", "", "Press any key to return."]);
    private void ShowInfo() => ShowOverlay("Book information", [
        $"Title:      {_book.Metadata.Title}", $"Author:     {Fallback(_book.Metadata.Creator)}",
        $"Language:   {Fallback(_book.Metadata.Language)}", $"Identifier: {Fallback(_book.Metadata.Identifier)}",
        $"Chapters:   {_book.ChapterCount}", $"File:       {_book.FilePath}", "", "Press any key to return."]);
    private void ShowOverlay(string title, IReadOnlyList<string> lines)
    {
        _screen.WriteRow(0, $" {title}", ConsoleColor.White, ConsoleColor.DarkBlue);
        for (int row = 0; row < BodyHeight; row++) _screen.WriteRow(row + 1, row < lines.Count ? $"  {lines[row]}" : "", ConsoleColor.Gray);
        _screen.WriteRow(_screen.Height - 1, " Any key returns to the book", ConsoleColor.White, ConsoleColor.DarkBlue); Console.ReadKey(true);
    }
    private void FlashMessage(string message)
    { _screen.WriteRow(_screen.Height - 1, $" {message}  Press any key.", ConsoleColor.White, ConsoleColor.DarkRed); Console.ReadKey(true); }
    private void SaveProgress()
    {
        double fraction = _wrappedLines.Count <= BodyHeight ? 0 : (double)_offset / (_wrappedLines.Count - BodyHeight);
        _progressStore.Save(_book.FilePath, new(_chapterIndex, fraction, DateTimeOffset.UtcNow));
    }
    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
