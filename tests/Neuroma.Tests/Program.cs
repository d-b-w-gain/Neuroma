using System.IO.Compression;
using System.Text;
using Neuroma.Epub;
using Neuroma.Storage;

namespace Neuroma.Tests;

internal static class Program
{
    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "neuroma-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string epubPath = Path.Combine(directory, "sample.epub"); CreateSampleEpub(epubPath);
            using EpubBook book = EpubLoader.Open(epubPath);
            Assert(book.Metadata.Title == "A Test Book", "reads title metadata");
            Assert(book.Metadata.Creator == "Ada Reader", "reads creator metadata");
            Assert(book.ChapterCount == 2, "follows the package spine");
            Assert(book.TableOfContents.Count == 2, "reads EPUB 3 navigation");
            Assert(book.GetChapter(0).Lines.Any(l => l.Contains("Hello terminal", StringComparison.Ordinal)), "renders XHTML text");
            Assert(book.GetChapter(0).Lines.Any(l => l.StartsWith("# ", StringComparison.Ordinal)), "renders heading semantics");
            Assert(book.GetChapter(1).Lines.Any(l => l.Contains("• Second item", StringComparison.Ordinal)), "renders lists");
            string progressPath = Path.Combine(directory, "progress.json"); var store = new ProgressStore(progressPath);
            store.Save(epubPath, new(1, 0.5, DateTimeOffset.UtcNow));
            ReadingPosition restored = new ProgressStore(progressPath).Get(epubPath);
            Assert(restored.Chapter == 1 && Math.Abs(restored.Fraction - 0.5) < 0.001, "persists reading position");
            Console.WriteLine("All Neuroma tests passed."); return 0;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static void Assert(bool condition, string behavior)
    { if (!condition) throw new InvalidOperationException($"Test failed: {behavior}"); Console.WriteLine($"PASS  {behavior}"); }
    private static void CreateSampleEpub(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add(archive, "META-INF/container.xml", """
            <?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
            <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
            """);
        Add(archive, "OEBPS/content.opf", """
            <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
            <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test</dc:identifier>
            <dc:title>A Test Book</dc:title><dc:creator>Ada Reader</dc:creator><dc:language>en</dc:language></metadata>
            <manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="one" href="text/one.xhtml" media-type="application/xhtml+xml"/>
            <item id="two" href="text/two.xhtml" media-type="application/xhtml+xml"/></manifest>
            <spine><itemref idref="one"/><itemref idref="two"/></spine></package>
            """);
        Add(archive, "OEBPS/nav.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
            <nav epub:type="toc"><ol><li><a href="text/one.xhtml">Opening</a></li>
            <li><a href="text/two.xhtml">Next steps</a></li></ol></nav></body></html>
            """);
        Add(archive, "OEBPS/text/one.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title></head>
            <body><h1>Opening</h1><p>Hello <em>terminal</em> reader.</p><blockquote>Readable and calm.</blockquote></body></html>
            """);
        Add(archive, "OEBPS/text/two.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml"><body><h1>Next steps</h1>
            <ul><li>First item</li><li>Second item</li></ul></body></html>
            """);
    }
    private static void Add(ZipArchive archive, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, level); using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(content);
    }
}

