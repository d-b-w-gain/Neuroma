using System.Text;
using Neuroma.Epub;
using Neuroma.Storage;
using Neuroma.Terminal;

namespace Neuroma;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Any(a => a is "--help" or "-h")) { PrintHelp(); return 0; }
        if (args.Any(a => a is "--version" or "-v")) { Console.WriteLine("Neuroma 0.1.1"); return 0; }

        bool plain = args.Any(a => a == "--plain") || Console.IsOutputRedirected;
        string? path = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(path))
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Neuroma: provide a path to an .epub file.");
                return 2;
            }
            Console.Write("EPUB path: ");
            path = Console.ReadLine()?.Trim().Trim('"');
        }
        if (string.IsNullOrWhiteSpace(path)) return 0;

        try
        {
            using EpubBook book = EpubLoader.Open(path);
            if (plain) { PrintPlain(book); return 0; }
            new ReaderApp(book, new ProgressStore()).Run();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EpubException)
        {
            Console.Error.WriteLine($"Neuroma: {ex.Message}");
            return 1;
        }
    }

    private static void PrintPlain(EpubBook book)
    {
        Console.WriteLine(book.Metadata.Title);
        if (!string.IsNullOrWhiteSpace(book.Metadata.Creator)) Console.WriteLine($"by {book.Metadata.Creator}");
        Console.WriteLine();
        for (int i = 0; i < book.ChapterCount; i++)
        {
            EpubChapter chapter = book.GetChapter(i);
            Console.WriteLine($"=== {chapter.Title} ===");
            foreach (string line in chapter.Lines) Console.WriteLine(line);
            Console.WriteLine();
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        Neuroma - read EPUB books in your terminal

        Usage:
          Neuroma <book.epub>
          Neuroma --plain <book.epub>

        Keys: arrows or j/k scroll; Space/PgUp/PgDn page; h/l change chapter;
              t contents; / search; n/N results; F11 maximize; ? help; q quit.
        """);
}
