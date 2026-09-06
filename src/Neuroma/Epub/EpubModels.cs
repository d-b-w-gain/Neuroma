namespace Neuroma.Epub;

public sealed record EpubMetadata(string Title, string Creator, string Language, string Identifier);
public sealed record EpubChapter(string Title, string EntryPath, IReadOnlyList<string> Lines);
public sealed record TocEntry(string Label, int ChapterIndex, string? Fragment, int Depth = 0);
internal sealed record ManifestItem(string Id, string EntryPath, string MediaType, string Properties);
internal sealed record ChapterDescriptor(string EntryPath, string? Label);

public class EpubException : Exception
{
    public EpubException(string message) : base(message) { }
    public EpubException(string message, Exception innerException) : base(message, innerException) { }
}

