namespace Neuroma.Epub;

public sealed record EpubMetadata(string Title, string Creator, string Language, string Identifier);
public abstract record EpubElement;
[Flags]
public enum EpubTextStyle : byte
{
    Normal = 0,
    Emphasis = 1,
    Strong = 2,
    Code = 4,
    Link = 8,
    Blockquote = 16,
    Dialogue = 32,
    DropCap = 64
}
public sealed record EpubText(string Text, IReadOnlyList<EpubTextStyle>? Styles = null) : EpubElement;
public sealed record EpubDropCap(
    string Prefix,
    char Initial,
    string Remainder,
    IReadOnlyList<EpubTextStyle>? Styles = null) : EpubElement
{
    public string Text => $"{Prefix}{Initial}{Remainder}";
}
public sealed record EpubImage(string EntryPath, string AltText) : EpubElement;
public sealed record EpubChapter(string Title, string EntryPath, IReadOnlyList<EpubElement> Elements)
{
    public IReadOnlyList<string> Lines => Elements.Select(element => element switch
    {
        EpubText text => text.Text,
        EpubDropCap dropCap => dropCap.Text,
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
