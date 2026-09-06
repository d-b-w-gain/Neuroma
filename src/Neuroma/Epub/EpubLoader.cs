using System.IO.Compression;
using System.Xml.Linq;

namespace Neuroma.Epub;

public static class EpubLoader
{
    public static EpubBook Open(string path)
    {
        if (!File.Exists(path)) throw new EpubException($"File not found: {path}");
        if (!string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new EpubException("The selected file is not an .epub file.");

        FileStream? stream = null;
        ZipArchive? archive = null;
        try
        {
            stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            RejectUnsupportedEncryption(archive);
            XDocument container = ReadXml(archive, "META-INF/container.xml");
            string? packagePath = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")
                ?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(packagePath))
                throw new EpubException("The EPUB container does not identify a package document.");

            packagePath = NormalizeArchivePath(packagePath);
            XDocument package = ReadXml(archive, packagePath);
            string packageDirectory = ArchiveDirectory(packagePath);
            EpubMetadata metadata = ParseMetadata(package);
            Dictionary<string, ManifestItem> manifest = ParseManifest(package, packageDirectory);
            List<ChapterDescriptor> chapters = ParseSpine(package, manifest);
            if (chapters.Count == 0) throw new EpubException("The EPUB has no readable chapters in its spine.");
            List<TocEntry> toc = ParseTableOfContents(archive, package, manifest, chapters);
            ApplyTocLabels(chapters, toc);

            var book = new EpubBook(path, stream, archive, metadata, chapters, toc);
            stream = null;
            archive = null;
            return book;
        }
        catch (EpubException) { throw; }
        catch (InvalidDataException ex) { throw new EpubException("The file is not a valid EPUB/ZIP archive.", ex); }
        catch (System.Xml.XmlException ex) { throw new EpubException("The EPUB contains malformed XML metadata.", ex); }
        finally { archive?.Dispose(); stream?.Dispose(); }
    }

    internal static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        string normalized = NormalizeArchivePath(path);
        return archive.Entries.FirstOrDefault(e =>
            string.Equals(NormalizeArchivePath(e.FullName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeArchivePath(string path)
    {
        string decoded;
        try { decoded = Uri.UnescapeDataString(path); } catch (UriFormatException) { decoded = path; }
        var parts = new List<string>();
        foreach (string part in decoded.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    internal static string ResolveResourcePath(string documentPath, string href) =>
        ResolveArchivePath(ArchiveDirectory(documentPath), StripFragment(href));

    private static EpubMetadata ParseMetadata(XDocument package)
    {
        XElement? metadata = package.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        string Value(string name) => metadata?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
        string title = Value("title");
        return new(string.IsNullOrWhiteSpace(title) ? "Untitled EPUB" : title,
            Value("creator"), Value("language"), Value("identifier"));
    }

    private static Dictionary<string, ManifestItem> ParseManifest(XDocument package, string packageDirectory)
    {
        XElement? manifest = package.Descendants().FirstOrDefault(e => e.Name.LocalName == "manifest");
        if (manifest is null) throw new EpubException("The EPUB package has no manifest.");
        var result = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (XElement item in manifest.Elements().Where(e => e.Name.LocalName == "item"))
        {
            string id = item.Attribute("id")?.Value ?? "";
            string href = item.Attribute("href")?.Value ?? "";
            if (id.Length == 0 || href.Length == 0) continue;
            result[id] = new(id, ResolveArchivePath(packageDirectory, StripFragment(href)),
                item.Attribute("media-type")?.Value ?? "", item.Attribute("properties")?.Value ?? "");
        }
        return result;
    }

    private static List<ChapterDescriptor> ParseSpine(XDocument package, Dictionary<string, ManifestItem> manifest)
    {
        XElement? spine = package.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spine is null) throw new EpubException("The EPUB package has no reading spine.");
        var primary = new List<ChapterDescriptor>();
        var secondary = new List<ChapterDescriptor>();
        foreach (XElement itemRef in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
        {
            string idRef = itemRef.Attribute("idref")?.Value ?? "";
            if (!manifest.TryGetValue(idRef, out ManifestItem? item) || !IsHtml(item)) continue;
            var descriptor = new ChapterDescriptor(item.EntryPath, null);
            if (string.Equals(itemRef.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase))
                secondary.Add(descriptor);
            else primary.Add(descriptor);
        }
        return primary.Count > 0 ? primary : secondary;
    }

    private static List<TocEntry> ParseTableOfContents(ZipArchive archive, XDocument package,
        Dictionary<string, ManifestItem> manifest, IReadOnlyList<ChapterDescriptor> chapters)
    {
        ManifestItem? nav = manifest.Values.FirstOrDefault(i =>
            i.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav"));
        if (nav is not null)
        {
            List<TocEntry> epub3 = ParseNavDocument(archive, nav.EntryPath, chapters);
            if (epub3.Count > 0) return epub3;
        }
        XElement? spine = package.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine");
        string? tocId = spine?.Attribute("toc")?.Value;
        ManifestItem? ncx = tocId is not null && manifest.TryGetValue(tocId, out ManifestItem? explicitNcx)
            ? explicitNcx : manifest.Values.FirstOrDefault(i => i.MediaType == "application/x-dtbncx+xml");
        return ncx is null ? [] : ParseNcx(archive, ncx.EntryPath, chapters);
    }

    private static List<TocEntry> ParseNavDocument(ZipArchive archive, string navPath,
        IReadOnlyList<ChapterDescriptor> chapters)
    {
        XDocument doc = ReadXml(archive, navPath);
        XElement? nav = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav" &&
            e.Attributes().Any(a => a.Name.LocalName == "type" && a.Value.Split(' ').Contains("toc")));
        nav ??= doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav");
        if (nav is null) return [];
        var result = new List<TocEntry>();
        WalkNav(nav, ArchiveDirectory(navPath), chapters, result, 0);
        return result;
    }

    private static void WalkNav(XElement node, string directory, IReadOnlyList<ChapterDescriptor> chapters,
        List<TocEntry> result, int depth)
    {
        foreach (XElement li in node.Elements().Where(e => e.Name.LocalName is "ol" or "ul")
                     .SelectMany(list => list.Elements().Where(e => e.Name.LocalName == "li")))
        {
            XElement? anchor = li.Elements().FirstOrDefault(e => e.Name.LocalName == "a");
            if (anchor is not null) AddTocEntry(anchor.Value, anchor.Attribute("href")?.Value, directory, chapters, result, depth);
            WalkNav(li, directory, chapters, result, depth + 1);
        }
    }

    private static List<TocEntry> ParseNcx(ZipArchive archive, string ncxPath,
        IReadOnlyList<ChapterDescriptor> chapters)
    {
        XDocument doc = ReadXml(archive, ncxPath);
        XElement? navMap = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");
        if (navMap is null) return [];
        var result = new List<TocEntry>();
        WalkNcx(navMap, ArchiveDirectory(ncxPath), chapters, result, 0);
        return result;
    }

    private static void WalkNcx(XElement node, string directory, IReadOnlyList<ChapterDescriptor> chapters,
        List<TocEntry> result, int depth)
    {
        foreach (XElement point in node.Elements().Where(e => e.Name.LocalName == "navPoint"))
        {
            string label = point.Descendants().FirstOrDefault(e => e.Name.LocalName == "text")?.Value ?? "Chapter";
            string? source = point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src")?.Value;
            AddTocEntry(label, source, directory, chapters, result, depth);
            WalkNcx(point, directory, chapters, result, depth + 1);
        }
    }

    private static void AddTocEntry(string label, string? href, string directory,
        IReadOnlyList<ChapterDescriptor> chapters, List<TocEntry> result, int depth)
    {
        if (string.IsNullOrWhiteSpace(href)) return;
        string path = ResolveArchivePath(directory, StripFragment(href));
        int index = chapters.ToList().FindIndex(c => string.Equals(c.EntryPath, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        string? fragment = href.Contains('#') ? href[(href.IndexOf('#') + 1)..] : null;
        result.Add(new(CleanWhitespace(label), index, fragment, depth));
    }

    private static void ApplyTocLabels(List<ChapterDescriptor> chapters, IReadOnlyList<TocEntry> toc)
    {
        foreach (IGrouping<int, TocEntry> group in toc.GroupBy(t => t.ChapterIndex))
            chapters[group.Key] = chapters[group.Key] with { Label = group.First().Label };
    }

    private static void RejectUnsupportedEncryption(ZipArchive archive)
    {
        ZipArchiveEntry? entry = FindEntry(archive, "META-INF/encryption.xml");
        if (entry is null) return;
        XDocument encryption;
        using (Stream stream = entry.Open()) encryption = XDocument.Load(stream);
        string[] fontAlgorithms = ["http://www.idpf.org/2008/embedding", "http://ns.adobe.com/pdf/enc#RC"];
        bool protectedContent = encryption.Descendants().Where(e => e.Name.LocalName == "EncryptionMethod")
            .Select(e => e.Attribute("Algorithm")?.Value ?? "")
            .Any(a => a.Length > 0 && !fontAlgorithms.Contains(a, StringComparer.OrdinalIgnoreCase));
        if (protectedContent) throw new EpubException("This EPUB contains DRM or encrypted content, which Neuroma cannot read.");
    }

    private static bool IsHtml(ManifestItem item) => item.MediaType is "application/xhtml+xml" or "text/html" ||
        item.EntryPath.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
        item.EntryPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        item.EntryPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = FindEntry(archive, path) ?? throw new EpubException($"The EPUB is missing '{path}'.");
        using Stream stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static string ResolveArchivePath(string directory, string href) =>
        NormalizeArchivePath(string.IsNullOrEmpty(directory) ? href : $"{directory}/{href}");
    private static string ArchiveDirectory(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : "";
    private static string StripFragment(string href)
    {
        int hash = href.IndexOf('#'); int query = href.IndexOf('?');
        int end = new[] { hash, query }.Where(i => i >= 0).DefaultIfEmpty(href.Length).Min();
        return href[..end];
    }
    private static string CleanWhitespace(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
