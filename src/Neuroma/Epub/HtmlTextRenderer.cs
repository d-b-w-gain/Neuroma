using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Neuroma.Epub;

public static partial class HtmlTextRenderer
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    { "address", "article", "aside", "div", "figure", "figcaption", "footer", "header", "main", "nav", "p", "section", "table", "tr" };

    public static IReadOnlyList<string> Render(string xhtml)
    {
        try
        {
            XDocument doc = XDocument.Parse(xhtml, LoadOptions.PreserveWhitespace);
            XElement? body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? doc.Root;
            if (body is null) return [];
            var output = new List<string>(); var inline = new StringBuilder();
            RenderChildren(body, output, inline, 0);
            Flush(output, inline);
            return Clean(output);
        }
        catch (System.Xml.XmlException) { return RenderFallback(xhtml); }
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

    private static void RenderChildren(XContainer parent, List<string> output, StringBuilder inline, int listDepth)
    {
        foreach (XNode node in parent.Nodes())
        {
            if (node is XText text) { Append(inline, text.Value); continue; }
            if (node is not XElement element) continue;
            string name = element.Name.LocalName.ToLowerInvariant();
            if (name is "script" or "style" or "head" or "svg" or "audio" or "video") continue;
            if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                Flush(output, inline); Blank(output); int level = name[1] - '0';
                output.Add($"{new string('#', level)} {Normalize(element.Value)}"); Blank(output);
            }
            else if (name == "br") Flush(output, inline);
            else if (name == "hr") { Flush(output, inline); output.Add("────────────────────────────────────────"); }
            else if (name == "img") Append(inline, $"[Image: {element.Attribute("alt")?.Value ?? "image"}]");
            else if (name == "li")
            {
                Flush(output, inline); var item = new StringBuilder();
                RenderChildren(element, output, item, listDepth + 1);
                if (item.Length > 0) output.Add($"{new string(' ', listDepth * 2)}• {Normalize(item.ToString())}");
            }
            else if (name == "blockquote")
            {
                Flush(output, inline); var quoteLines = new List<string>(); var quote = new StringBuilder();
                RenderChildren(element, quoteLines, quote, listDepth); Flush(quoteLines, quote);
                foreach (string line in Clean(quoteLines)) output.Add(line.Length == 0 ? "│" : $"│ {line}");
                Blank(output);
            }
            else if (name == "pre")
            {
                Flush(output, inline); Blank(output);
                foreach (string line in element.Value.Replace("\r", "").Split('\n')) output.Add($"    {line.TrimEnd()}");
                Blank(output);
            }
            else if (Blocks.Contains(name))
            {
                Flush(output, inline); RenderChildren(element, output, inline, listDepth); Flush(output, inline); Blank(output);
            }
            else RenderChildren(element, output, inline, listDepth);
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        string text = Normalize(value); if (text.Length == 0) return;
        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]) && !char.IsPunctuation(text[0])) builder.Append(' ');
        builder.Append(text);
    }
    private static void Flush(List<string> output, StringBuilder inline)
    { string value = Normalize(inline.ToString()); if (value.Length > 0) output.Add(value); inline.Clear(); }
    private static void Blank(List<string> output) { if (output.Count > 0 && output[^1].Length > 0) output.Add(""); }
    private static IReadOnlyList<string> Clean(List<string> lines)
    {
        var clean = new List<string>();
        foreach (string line in lines)
        { string value = line.TrimEnd(); if (value.Length == 0 && (clean.Count == 0 || clean[^1].Length == 0)) continue; clean.Add(value); }
        while (clean.Count > 0 && clean[^1].Length == 0) clean.RemoveAt(clean.Count - 1);
        return clean.Count == 0 ? ["(This chapter contains no displayable text.)"] : clean;
    }
    private static IReadOnlyList<string> RenderFallback(string html)
    {
        string text = BreakTags().Replace(html, "\n"); text = AnyTag().Replace(text, ""); text = WebUtility.HtmlDecode(text);
        List<string> lines = text.Replace("\r", "").Split('\n').Select(Normalize).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? ["(This chapter contains no displayable text.)"] : lines;
    }
    private static string Normalize(string value) => Whitespace().Replace(WebUtility.HtmlDecode(value), " ").Trim();
    [GeneratedRegex(@"</?(?:p|div|section|article|aside|header|footer|main|nav|h[1-6]|li|blockquote|pre|br|hr|tr)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BreakTags();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)] private static partial Regex AnyTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
