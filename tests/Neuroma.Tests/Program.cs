using System.IO.Compression;
using System.Text;
using Neuroma.Epub;
using Neuroma.Storage;
using Neuroma.Speech;
using Neuroma.Terminal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Neuroma.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1) return CheckExternalBook(args[0]);
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
            EpubImage cover = book.GetChapter(0).Elements.OfType<EpubImage>().Single();
            Assert(cover.EntryPath == "OEBPS/images/cover.png", "resolves EPUB image paths");
            byte[] coverData = book.ReadResource(cover.EntryPath) ?? throw new InvalidOperationException("Cover data missing");
            IReadOnlyList<string> imageRows = TerminalImageRenderer.Render(coverData, 20, 10);
            Assert(imageRows.Count > 0 && imageRows.Any(row => row.Contains("\x1b[", StringComparison.Ordinal)), "renders image as ANSI colour cells");
            IReadOnlyList<EpubElement> svgCover = HtmlTextRenderer.Render("""
                <html xmlns="http://www.w3.org/1999/xhtml"><body><svg xmlns="http://www.w3.org/2000/svg">
                <image href="../images/cover.png"/></svg></body></html>
                """, "OEBPS/text/cover.xhtml");
            Assert(svgCover.OfType<EpubImage>().Single().EntryPath == "OEBPS/images/cover.png", "reads SVG-wrapped cover images");
            string progressPath = Path.Combine(directory, "progress.json"); var store = new ProgressStore(progressPath);
            store.Save(epubPath, new(1, 0.5, DateTimeOffset.UtcNow));
            ReadingPosition restored = new ProgressStore(progressPath).Get(epubPath);
            Assert(restored.Chapter == 1 && Math.Abs(restored.Fraction - 0.5) < 0.001, "persists reading position");
            SpeechSettings speech = SpeechSettings.Load([
                "--kokoro-url", "http://localhost:9999/", "--voice=af_test", "--speed", "1.5", epubPath
            ], out HashSet<int> consumed);
            Assert(speech.KokoroUrl == "http://localhost:9999" && speech.Voice == "af_test" &&
                Math.Abs(speech.Speed - 1.5) < 0.001, "loads Kokoro command-line settings");
            Assert(consumed.SetEquals([0, 1, 2, 3, 4]), "keeps the EPUB path separate from speech options");
            Console.WriteLine("All Neuroma tests passed."); return 0;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static int CheckExternalBook(string path)
    {
        using EpubBook book = EpubLoader.Open(path);
        int referenced = 0, rendered = 0, unavailable = 0;
        for (int chapterIndex = 0; chapterIndex < book.ChapterCount; chapterIndex++)
        {
            foreach (EpubImage image in book.GetChapter(chapterIndex).Elements.OfType<EpubImage>())
            {
                referenced++;
                byte[]? data = book.ReadResource(image.EntryPath);
                if (data is null) { unavailable++; Console.WriteLine($"MISSING {image.EntryPath}"); continue; }
                try { if (TerminalImageRenderer.Render(data, 30, 12).Count > 0) rendered++; }
                catch (Exception ex) { unavailable++; Console.WriteLine($"FALLBACK {image.EntryPath}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        Console.WriteLine($"Checked {book.Metadata.Title}: {rendered}/{referenced} images rendered; {unavailable} fallbacks.");
        return unavailable == 0 ? 0 : 1;
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
            <body><img src="../images/cover.png" alt="Test cover"/><h1>Opening</h1>
            <p>Hello <em>terminal</em> reader.</p><blockquote>Readable and calm.</blockquote></body></html>
            """);
        Add(archive, "OEBPS/text/two.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml"><body><h1>Next steps</h1>
            <ul><li>First item</li><li>Second item</li></ul></body></html>
            """);
        Add(archive, "OEBPS/images/cover.png", CreateTestPng());
    }
    private static void Add(ZipArchive archive, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, level); using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(content);
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open(); stream.Write(content);
    }

    private static byte[] CreateTestPng()
    {
        using var image = new Image<Rgba32>(8, 12);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                image[x, y] = x < image.Width / 2 ? new Rgba32(20, 180, 240) : new Rgba32(170, 60, 230);
        using var stream = new MemoryStream(); image.SaveAsPng(stream); return stream.ToArray();
    }
}
