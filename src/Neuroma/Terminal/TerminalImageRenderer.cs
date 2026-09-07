using Spectre.Console;
using Neuroma.Epub;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Neuroma.Terminal;

public static class TerminalImageRenderer
{
    public static IReadOnlyList<string> Render(byte[] imageData, int maxColumns, int maxRows)
        => RenderBlock(imageData, maxColumns, maxRows).Rows;

    internal static TerminalImageBlock RenderBlock(byte[] imageData, int maxColumns, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        maxColumns = Math.Max(1, maxColumns);
        maxRows = Math.Max(1, maxRows);

        using var output = new StringWriter();
        var settings = new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output)
        };
        IAnsiConsole console = AnsiConsole.Create(settings);
        console.Profile.Width = maxColumns;

        using var source = SixLabors.ImageSharp.Image.Load<Rgba32>(imageData);
        int targetWidth = Math.Max(1, Math.Min(source.Width, maxColumns));
        int targetHeight = Math.Max(2, (int)Math.Round(source.Height * (double)targetWidth / source.Width));
        int maximumPixelHeight = Math.Max(2, maxRows * 2);
        if (targetHeight > maximumPixelHeight)
        {
            targetHeight = maximumPixelHeight;
            targetWidth = Math.Max(1, (int)Math.Round(source.Width * (double)targetHeight / source.Height));
        }

        source.Mutate(context => context.Resize(targetWidth, targetHeight));
        using var buffer = new MemoryStream();
        source.SaveAsPng(buffer);
        var image = new CanvasImage(buffer.ToArray()) { MaxWidth = targetWidth };
        console.Write(image);

        IReadOnlyList<string> rows = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n')
            .Split('\n').ToList();
        return new TerminalImageBlock(rows, targetWidth);
    }
}

internal sealed record TerminalImageBlock(IReadOnlyList<string> Rows, int Width);

internal sealed record DisplayLine(
    string Content,
    bool IsImage = false,
    string? SpokenText = null,
    IReadOnlyList<int>? SpokenColumnMap = null,
    IReadOnlyList<EpubTextStyle>? Styles = null,
    int ParagraphId = -1,
    string? AnsiOverlay = null,
    int AnsiOverlayColumn = 0)
{
    public string SearchText => IsImage ? "" : SpokenText ?? Content;
}
