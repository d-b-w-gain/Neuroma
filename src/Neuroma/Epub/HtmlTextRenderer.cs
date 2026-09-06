using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Neuroma.Epub;

public static partial class HtmlTextRenderer
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    { "address", "article", "aside", "div", "figure", "figcaption", "footer", "header", "main", "nav", "p", "section", "table", "tr", "ul", "ol" };

    public static IReadOnlyList<EpubElement> Render(string xhtml, string documentPath)
    {
        try
        {
            XDocument doc = XDocument.Parse(xhtml, LoadOptions.PreserveWhitespace);
            XElement? body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? doc.Root;
            if (body is null) return [];
            var output = new List<EpubElement>();
            var inline = new StringBuilder();
            RenderChildren(body, documentPath, output, inline, 0);
            Flush(output, inline);
            return Clean(output);
        }
        catch (System.Xml.XmlException)
        {
            return RenderFallback(xhtml);
        }
    }

    public static string? ExtractTitle(string xhtml)
    {
        try
        {
            XDocument doc = XDocument.Parse(xhtml);
            string? title = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "h1" or "h2")?.Value
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
            return string.IsNullOrWhiteSpace(title) ? null : Normalize(title);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static void RenderChildren(XContainer parent, string documentPath, List<EpubElement> output,
        StringBuilder inline, int listDepth)
    {
        foreach (XNode node in parent.Nodes())
        {
            if (node is XText text) { Append(inline, text.Value); continue; }
            if (node is not XElement element) continue;
            string name = element.Name.LocalName.ToLowerInvariant();
            if (name is "script" or "style" or "head" or "audio" or "video") continue;

            if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                Flush(output, inline); Blank(output);
                output.Add(new EpubText($"{new string('#', name[1] - '0')} {Normalize(element.Value)}"));
                Blank(output);
            }
            else if (name == "br") Flush(output, inline);
            else if (name == "hr") { Flush(output, inline); output.Add(new EpubText("────────────────────────────────────────")); }
            else if (name == "img") AddImage(element.Attribute("src")?.Value, element.Attribute("alt")?.Value, documentPath, output, inline);
            else if (name == "object") AddImage(element.Attribute("data")?.Value, element.Attribute("title")?.Value, documentPath, output, inline);
            else if (name == "svg")
            {
                Flush(output, inline);
                foreach (XElement image in element.Descendants().Where(e => e.Name.LocalName == "image"))
                {
                    string? source = image.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
                    AddImage(source, image.Attribute("aria-label")?.Value, documentPath, output, inline);
                }
            }
            else if (name == "li") RenderListItem(element, documentPath, output, inline, listDepth);
            else if (name == "blockquote") RenderQuote(element, documentPath, output, inline, listDepth);
            else if (name == "pre")
            {
                Flush(output, inline); Blank(output);
                foreach (string line in element.Value.Replace("\r", "").Split('\n')) output.Add(new EpubText($"    {line.TrimEnd()}"));
                Blank(output);
            }
            else if (name == "p" && TryCreateDropCap(element, out EpubDropCap? dropCap) && dropCap is not null)
            {
                Flush(output, inline); output.Add(dropCap); Blank(output);
            }
            else if (Blocks.Contains(name))
            {
                Flush(output, inline); RenderChildren(element, documentPath, output, inline, listDepth);
                Flush(output, inline); Blank(output);
            }
            else RenderChildren(element, documentPath, output, inline, listDepth);
        }
    }

    private static void AddImage(string? source, string? alt, string documentPath, List<EpubElement> output, StringBuilder inline)
    {
        Flush(output, inline);
        string label = string.IsNullOrWhiteSpace(alt) ? "image" : Normalize(alt);
        string path = string.IsNullOrWhiteSpace(source) || Uri.TryCreate(source, UriKind.Absolute, out _)
            ? "" : EpubLoader.ResolveResourcePath(documentPath, source);
        output.Add(new EpubImage(path, label));
        Blank(output);
    }

    private static void RenderListItem(XElement element, string documentPath, List<EpubElement> output,
        StringBuilder inline, int listDepth)
    {
        Flush(output, inline);
        var item = new List<EpubElement>(); var builder = new StringBuilder();
        RenderChildren(element, documentPath, item, builder, listDepth + 1); Flush(item, builder);
        bool firstText = true;
        foreach (EpubElement child in Clean(item))
        {
            if (child is EpubText text && text.Text.Length > 0)
            {
                string prefix = firstText ? $"{new string(' ', listDepth * 2)}• " : new string(' ', listDepth * 2 + 2);
                output.Add(new EpubText(prefix + text.Text)); firstText = false;
            }
            else output.Add(child);
        }
    }

    private static void RenderQuote(XElement element, string documentPath, List<EpubElement> output,
        StringBuilder inline, int listDepth)
    {
        Flush(output, inline); var quote = new List<EpubElement>(); var builder = new StringBuilder();
        RenderChildren(element, documentPath, quote, builder, listDepth); Flush(quote, builder);
        foreach (EpubElement child in Clean(quote))
            output.Add(child is EpubText text ? new EpubText(text.Text.Length == 0 ? "│" : $"│ {text.Text}") : child);
        Blank(output);
    }

    private static void Append(StringBuilder builder, string value)
    {
        bool leadingSpace = value.Length > 0 && char.IsWhiteSpace(value[0]);
        bool trailingSpace = value.Length > 0 && char.IsWhiteSpace(value[^1]);
        string text = Normalize(value);
        if (text.Length == 0)
        {
            if ((leadingSpace || trailingSpace) && builder.Length > 0 && !char.IsWhiteSpace(builder[^1])) builder.Append(' ');
            return;
        }
        if (leadingSpace && builder.Length > 0 && !char.IsWhiteSpace(builder[^1])) builder.Append(' ');
        builder.Append(text);
        if (trailingSpace) builder.Append(' ');
    }

    private static bool TryCreateDropCap(XElement paragraph, out EpubDropCap? dropCap)
    {
        dropCap = null;
        XElement? smallCap = paragraph.Descendants().FirstOrDefault(element =>
            (element.Attribute("class")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(value => value.Equals("smallcap", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("scap", StringComparison.OrdinalIgnoreCase)));
        if (smallCap?.Parent is not XElement lead) return false;

        string paragraphText = Normalize(paragraph.Value);
        string leadText = Normalize(lead.Value);
        if (paragraphText.Length == 0 || leadText.Length == 0 ||
            !paragraphText.StartsWith(leadText, StringComparison.Ordinal)) return false;

        int initialIndex = 0;
        while (initialIndex < paragraphText.Length && !char.IsLetter(paragraphText[initialIndex])) initialIndex++;
        if (initialIndex > 2 || initialIndex >= paragraphText.Length) return false;
        string smallCapText = Normalize(smallCap.Value);
        if (smallCapText.Length == 0 || !paragraphText.AsSpan(initialIndex + 1).StartsWith(smallCapText, StringComparison.Ordinal))
            return false;

        dropCap = new EpubDropCap(paragraphText[..initialIndex], paragraphText[initialIndex], paragraphText[(initialIndex + 1)..]);
        return true;
    }

    private static void Flush(List<EpubElement> output, StringBuilder inline)
    {
        string value = Normalize(inline.ToString()); if (value.Length > 0) output.Add(new EpubText(value)); inline.Clear();
    }

    private static void Blank(List<EpubElement> output)
    {
        if (output.Count > 0 && output[^1] is not EpubText { Text.Length: 0 }) output.Add(new EpubText(""));
    }

    private static IReadOnlyList<EpubElement> Clean(List<EpubElement> elements)
    {
        var clean = new List<EpubElement>();
        foreach (EpubElement element in elements)
        {
            if (element is EpubText text)
            {
                string value = text.Text.TrimEnd();
                if (value.Length == 0 && (clean.Count == 0 || clean[^1] is EpubText { Text.Length: 0 })) continue;
                clean.Add(new EpubText(value));
            }
            else clean.Add(element);
        }
        while (clean.Count > 0 && clean[^1] is EpubText { Text.Length: 0 }) clean.RemoveAt(clean.Count - 1);
        return clean.Count == 0 ? [new EpubText("(This chapter contains no displayable content.)")] : clean;
    }

    private static IReadOnlyList<EpubElement> RenderFallback(string html)
    {
        string text = BreakTags().Replace(html, "\n"); text = AnyTag().Replace(text, ""); text = WebUtility.HtmlDecode(text);
        List<EpubElement> lines = text.Replace("\r", "").Split('\n').Select(Normalize).Where(l => l.Length > 0)
            .Select(l => (EpubElement)new EpubText(l)).ToList();
        return lines.Count == 0 ? [new EpubText("(This chapter contains no displayable content.)")] : lines;
    }

    private static string Normalize(string value) => Whitespace().Replace(WebUtility.HtmlDecode(value), " ").Trim();
    [GeneratedRegex(@"</?(?:p|div|section|article|aside|header|footer|main|nav|h[1-6]|li|blockquote|pre|br|hr|tr)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BreakTags();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)] private static partial Regex AnyTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
