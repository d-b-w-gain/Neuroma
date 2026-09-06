using System.IO.Compression;

namespace Neuroma.Epub;

public sealed class EpubBook : IDisposable
{
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly IReadOnlyList<ChapterDescriptor> _chapters;
    private readonly Dictionary<int, EpubChapter> _cache = [];

    internal EpubBook(string filePath, FileStream stream, ZipArchive archive, EpubMetadata metadata,
        IReadOnlyList<ChapterDescriptor> chapters, IReadOnlyList<TocEntry> tableOfContents)
    {
        FilePath = Path.GetFullPath(filePath);
        _stream = stream;
        _archive = archive;
        Metadata = metadata;
        _chapters = chapters;
        TableOfContents = tableOfContents;
    }

    public string FilePath { get; }
    public EpubMetadata Metadata { get; }
    public IReadOnlyList<TocEntry> TableOfContents { get; }
    public int ChapterCount => _chapters.Count;

    public EpubChapter GetChapter(int index)
    {
        if (index < 0 || index >= _chapters.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_cache.TryGetValue(index, out EpubChapter? cached)) return cached;
        ChapterDescriptor descriptor = _chapters[index];
        ZipArchiveEntry entry = EpubLoader.FindEntry(_archive, descriptor.EntryPath)
            ?? throw new EpubException($"The chapter '{descriptor.EntryPath}' is missing from the EPUB archive.");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        string xhtml = reader.ReadToEnd();
        IReadOnlyList<string> lines = HtmlTextRenderer.Render(xhtml);
        string title = descriptor.Label ?? HtmlTextRenderer.ExtractTitle(xhtml) ?? $"Chapter {index + 1}";
        return _cache[index] = new EpubChapter(title, descriptor.EntryPath, lines);
    }

    public void Dispose() { _archive.Dispose(); _stream.Dispose(); }
}
