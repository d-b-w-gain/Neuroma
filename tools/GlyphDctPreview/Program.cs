using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;

namespace GlyphDctPreview;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int PatchWidth = 8;
    private const int PatchHeight = 16;
    private static int OutputColumns;
    private static int OutputRows;

    private static int Main(string[] args)
    {
        if (args.Length < 5 || !int.TryParse(args[2], out OutputColumns) ||
            !int.TryParse(args[3], out OutputRows) || OutputColumns < 1 || OutputRows < 1)
        {
            Console.Error.WriteLine("Usage: GlyphDctPreview FONT OUTPUT_DIRECTORY COLUMNS ROWS IMAGE...");
            return 2;
        }

        string fontPath = Path.GetFullPath(args[0]);
        string outputDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outputDirectory);
        using var fonts = new PrivateFontCollection();
        fonts.AddFontFile(fontPath);
        FontFamily family = fonts.Families[0];
        GlyphCandidate[] atlas = BuildAtlas(family);

        foreach (string input in args.Skip(4))
        {
            string sourcePath = Path.GetFullPath(input);
            Render(sourcePath, outputDirectory, family, atlas);
        }
        return 0;
    }

    private static GlyphCandidate[] BuildAtlas(FontFamily family)
    {
        const int rasterHeight = 64;
        int rasterWidth = rasterHeight / 2;
        using var font = new Font(family, 52, FontStyle.Regular, GraphicsUnit.Pixel);
        double[] missingGlyph = RasterizeGlyph("\u0378", font, rasterWidth, rasterHeight);
        var result = new List<GlyphCandidate>();
        foreach (int codePoint in CandidateCodePoints())
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Surrogate or
                UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;

            string symbol = char.ConvertFromUtf32(codePoint);
            double[] mask = RasterizeGlyph(symbol, font, rasterWidth, rasterHeight);
            double energy = Dot(mask, mask);
            if (symbol != " " && (energy < 0.000001 || SquaredDifference(mask, missingGlyph) < 0.000001)) continue;
            double[] transformed = Dct2(mask, PatchWidth, PatchHeight);
            result.Add(new GlyphCandidate(symbol, transformed, Dot(transformed, transformed)));
        }
        return result.ToArray();
    }

    private static IEnumerable<int> CandidateCodePoints()
    {
        // These are terminal-safe, single-cell blocks in Cascadia Mono. Keeping the
        // atlas to one-cell symbols preserves the 28-column layout in Windows Terminal.
        (int Start, int End)[] ranges =
        [
            (0x0020, 0x007e), // printable ASCII
            (0x2190, 0x21ff), // arrows
            (0x2300, 0x23ff), // miscellaneous technical
            (0x2500, 0x257f), // box drawing
            (0x2580, 0x259f), // block elements
            (0x25a0, 0x25ff), // geometric shapes
            (0x2800, 0x28ff)  // Braille patterns
        ];
        foreach ((int start, int end) in ranges)
            for (int codePoint = start; codePoint <= end; codePoint++) yield return codePoint;
    }

    private static double[] RasterizeGlyph(string symbol, Font font, int rasterWidth, int rasterHeight)
    {
        using var raster = new Bitmap(rasterWidth, rasterHeight);
        using (Graphics graphics = Graphics.FromImage(raster))
        {
            graphics.Clear(Color.Black);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(symbol, font, Brushes.White,
                new RectangleF(0, 0, rasterWidth, rasterHeight), StringFormat.GenericTypographic);
        }
        return ResizeToPatch(raster, PatchWidth, PatchHeight);
    }

    private static void Render(string sourcePath, string outputDirectory, FontFamily family, GlyphCandidate[] atlas)
    {
        using var source = new Bitmap(sourcePath);
        using Bitmap cleaned = RemoveNearWhiteBackground(source);
        Rectangle bounds = FindVisibleBounds(cleaned);
        using Bitmap fitted = FitSource(cleaned, bounds, OutputColumns * PatchWidth, OutputRows * PatchHeight);
        var cells = new MatchedCell[OutputRows, OutputColumns];
        var patches = new double[OutputRows * OutputColumns][][];

        for (int cellY = 0; cellY < OutputRows; cellY++)
        for (int cellX = 0; cellX < OutputColumns; cellX++)
        {
            double[][] patch = ReadColorPatch(fitted, cellX * PatchWidth, cellY * PatchHeight);
            patches[cellY * OutputColumns + cellX] =
                patch.Select(channel => Dct2(channel, PatchWidth, PatchHeight)).ToArray();
        }

        Parallel.For(0, patches.Length, index =>
        {
            int row = index / OutputColumns;
            int column = index % OutputColumns;
            cells[row, column] = Match(patches[index], atlas);
        });

        string stem = Path.GetFileNameWithoutExtension(sourcePath) + $"-dct-{OutputColumns}x{OutputRows}";
        WriteText(Path.Combine(outputDirectory, stem + ".txt"), cells);
        WriteAnsi(Path.Combine(outputDirectory, stem + ".ans"), cells);
        WritePreview(Path.Combine(outputDirectory, stem + ".png"), cells, family);
        Console.WriteLine($"Rendered {stem}: {OutputColumns}x{OutputRows}, {atlas.Length} glyph candidates, {PatchWidth}x{PatchHeight} DCT.");
    }

    private static Bitmap RemoveNearWhiteBackground(Bitmap source)
    {
        var cleaned = new Bitmap(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            Color pixel = source.GetPixel(x, y);
            int darkest = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            int matteAlpha = Math.Clamp((225 - darkest) * 12, 0, 255);
            cleaned.SetPixel(x, y, Color.FromArgb(Math.Min(pixel.A, matteAlpha), pixel.R, pixel.G, pixel.B));
        }
        return cleaned;
    }

    private static MatchedCell Match(double[][] patch, IReadOnlyList<GlyphCandidate> atlas)
    {
        GlyphCandidate best = atlas[0];
        double bestScore = double.PositiveInfinity;
        double[] bestColor = [0, 0, 0];
        double[] patchEnergy = patch.Select(channel => Dot(channel, channel)).ToArray();
        foreach (GlyphCandidate glyph in atlas)
        {
            double score = 0;
            double[] color = [0, 0, 0];
            for (int channel = 0; channel < 3; channel++)
            {
                if (glyph.Energy < 0.000001)
                {
                    score += patchEnergy[channel];
                    continue;
                }
                double correlation = Dot(patch[channel], glyph.Dct);
                double foreground = Math.Clamp(correlation / glyph.Energy, 0, 255);
                color[channel] = foreground;
                score += patchEnergy[channel] - 2 * foreground * correlation + foreground * foreground * glyph.Energy;
            }
            if (score >= bestScore) continue;
            best = glyph;
            bestScore = score;
            bestColor = color;
        }
        return new MatchedCell(best.Symbol,
            Color.FromArgb((int)Math.Round(bestColor[0]), (int)Math.Round(bestColor[1]), (int)Math.Round(bestColor[2])));
    }

    private static Bitmap FitSource(Bitmap source, Rectangle bounds, int width, int height)
    {
        int padding = Math.Max(2, (int)Math.Round(Math.Max(bounds.Width, bounds.Height) * 0.025));
        bounds.Inflate(padding, padding);
        bounds.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        var output = new Bitmap(width, height);
        using Graphics graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Black);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        double scale = Math.Min((double)width / bounds.Width, (double)height / bounds.Height);
        int targetWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        int left = (width - targetWidth) / 2;
        int top = (height - targetHeight) / 2;
        graphics.DrawImage(source, new Rectangle(left, top, targetWidth, targetHeight), bounds, GraphicsUnit.Pixel);
        return output;
    }

    private static Rectangle FindVisibleBounds(Bitmap image)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            if (image.GetPixel(x, y).A <= 12) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? new Rectangle(0, 0, image.Width, image.Height) :
            Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static double[][] ReadColorPatch(Bitmap image, int left, int top)
    {
        double[][] channels = [new double[PatchWidth * PatchHeight], new double[PatchWidth * PatchHeight],
            new double[PatchWidth * PatchHeight]];
        for (int y = 0; y < PatchHeight; y++)
        for (int x = 0; x < PatchWidth; x++)
        {
            Color pixel = image.GetPixel(left + x, top + y);
            double alpha = pixel.A / 255.0;
            int index = y * PatchWidth + x;
            channels[0][index] = pixel.R * alpha;
            channels[1][index] = pixel.G * alpha;
            channels[2][index] = pixel.B * alpha;
        }
        return channels;
    }

    private static double[] ResizeToPatch(Bitmap source, int width, int height)
    {
        using var resized = new Bitmap(width, height);
        using (Graphics graphics = Graphics.FromImage(resized))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        var result = new double[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++) result[y * width + x] = resized.GetPixel(x, y).R / 255.0;
        return result;
    }

    private static double[] Dct2(double[] source, int width, int height)
    {
        var result = new double[source.Length];
        for (int v = 0; v < height; v++)
        for (int u = 0; u < width; u++)
        {
            double sum = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                sum += source[y * width + x] *
                    Math.Cos(Math.PI * (2 * x + 1) * u / (2 * width)) *
                    Math.Cos(Math.PI * (2 * y + 1) * v / (2 * height));
            double scaleX = u == 0 ? Math.Sqrt(1.0 / width) : Math.Sqrt(2.0 / width);
            double scaleY = v == 0 ? Math.Sqrt(1.0 / height) : Math.Sqrt(2.0 / height);
            result[v * width + u] = sum * scaleX * scaleY;
        }
        return result;
    }

    private static double Dot(double[] left, double[] right)
    {
        int width = Vector<double>.Count;
        var vectorSum = Vector<double>.Zero;
        int index = 0;
        for (; index <= left.Length - width; index += width)
            vectorSum += new Vector<double>(left, index) * new Vector<double>(right, index);
        double sum = Vector.Dot(vectorSum, Vector<double>.One);
        for (; index < left.Length; index++) sum += left[index] * right[index];
        return sum;
    }

    private static double SquaredDifference(double[] left, double[] right)
    {
        double sum = 0;
        for (int index = 0; index < left.Length; index++)
        {
            double difference = left[index] - right[index];
            sum += difference * difference;
        }
        return sum;
    }

    private static void WriteText(string path, MatchedCell[,] cells)
    {
        var lines = new string[OutputRows];
        for (int row = 0; row < OutputRows; row++)
        {
            var value = new StringBuilder();
            for (int column = 0; column < OutputColumns; column++) value.Append(cells[row, column].Symbol);
            lines[row] = value.ToString().TrimEnd();
        }
        File.WriteAllLines(path, lines);
    }

    private static void WriteAnsi(string path, MatchedCell[,] cells)
    {
        var output = new StringBuilder();
        for (int row = 0; row < OutputRows; row++)
        {
            for (int column = 0; column < OutputColumns; column++)
            {
                MatchedCell cell = cells[row, column];
                output.Append($"\x1b[38;2;{cell.Color.R};{cell.Color.G};{cell.Color.B}m{cell.Symbol}");
            }
            output.Append("\x1b[0m\n");
        }
        File.WriteAllText(path, output.ToString());
    }

    private static void WritePreview(string path, MatchedCell[,] cells, FontFamily family)
    {
        const int cellWidth = 20;
        const int cellHeight = 40;
        using var image = new Bitmap(OutputColumns * cellWidth, OutputRows * cellHeight);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Black);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(family, 32, FontStyle.Regular, GraphicsUnit.Pixel);
        for (int row = 0; row < OutputRows; row++)
        for (int column = 0; column < OutputColumns; column++)
        {
            MatchedCell cell = cells[row, column];
            using var brush = new SolidBrush(cell.Color);
            graphics.DrawString(cell.Symbol, font, brush,
                new RectangleF(column * cellWidth, row * cellHeight, cellWidth, cellHeight),
                StringFormat.GenericTypographic);
        }
        image.Save(path);
    }

    private sealed record GlyphCandidate(string Symbol, double[] Dct, double Energy);
    private sealed record MatchedCell(string Symbol, Color Color);
}
