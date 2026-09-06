namespace Neuroma.Epub;

public sealed record EpubMetadata(string Title, string Creator, string Language, string Identifier);
public abstract record EpubElement;
public sealed record EpubText(string Text) : EpubElement;
public sealed record EpubImage(string EntryPath, string AltText) : EpubElement;
public sealed record EpubChapter(string Title, string EntryPath, IReadOnlyList<EpubElement> Elements)
{
    public IReadOnlyList<string> Lines => Elements.Select(element => element switch
    {
        EpubText text => text.Text,
        EpubImage image => $"[Image: {image.AltText}]",
        _ => ""
    }).ToList();
}
public sealed record TocEntry(string Label, int ChapterIndex, string? Fragment, int Depth = 0);
internal sealed record ManifestItem(string Id, string EntryPath, string MediaType, string Properties);
internal sealed record ChapterDescriptor(string EntryPath, string? Label);

public class EpubException : Exception
{
    public EpubException(string message) : base(message) { }
    public EpubException(string message, Exception innerException) : base(message, innerException) { }
}
