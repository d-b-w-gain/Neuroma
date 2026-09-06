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
            var inline = new InlineTextBuilder();
            RenderChildren(body, documentPath, output, inline, 0, EpubTextStyle.Normal);
            Flush(output, inline);
            return ApplyDialogueStyles(Clean(output));
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
        InlineTextBuilder inline, int listDepth, EpubTextStyle inheritedStyle)
    {
        foreach (XNode node in parent.Nodes())
        {
            if (node is XText text) { inline.Append(text.Value, inheritedStyle); continue; }
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
            else if (name == "li") RenderListItem(element, documentPath, output, inline, listDepth, inheritedStyle);
            else if (name == "blockquote") RenderQuote(element, documentPath, output, inline, listDepth, inheritedStyle);
            else if (name == "pre")
            {
                Flush(output, inline); Blank(output);
                foreach (string line in element.Value.Replace("\r", "").Split('\n'))
                    output.Add(StyledText($"    {line.TrimEnd()}", EpubTextStyle.Code));
                Blank(output);
            }
            else if (name == "p" && TryCreateDropCap(element, out EpubDropCap? dropCap) && dropCap is not null)
            {
                Flush(output, inline); output.Add(dropCap); Blank(output);
            }
            else if (Blocks.Contains(name))
            {
                Flush(output, inline); RenderChildren(element, documentPath, output, inline, listDepth, inheritedStyle);
                Flush(output, inline); Blank(output);
            }
            else
            {
                EpubTextStyle childStyle = inheritedStyle | (name switch
                {
                    "i" or "em" => EpubTextStyle.Emphasis,
                    "b" or "strong" => EpubTextStyle.Strong,
                    "code" or "kbd" or "samp" => EpubTextStyle.Code,
                    "a" => EpubTextStyle.Link,
                    _ => EpubTextStyle.Normal
                });
                RenderChildren(element, documentPath, output, inline, listDepth, childStyle);
            }
        }
    }

    private static void AddImage(string? source, string? alt, string documentPath, List<EpubElement> output, InlineTextBuilder inline)
    {
        Flush(output, inline);
        string label = string.IsNullOrWhiteSpace(alt) ? "image" : Normalize(alt);
        string path = string.IsNullOrWhiteSpace(source) || Uri.TryCreate(source, UriKind.Absolute, out _)
            ? "" : EpubLoader.ResolveResourcePath(documentPath, source);
        output.Add(new EpubImage(path, label));
        Blank(output);
    }

    private static void RenderListItem(XElement element, string documentPath, List<EpubElement> output,
        InlineTextBuilder inline, int listDepth, EpubTextStyle inheritedStyle)
    {
        Flush(output, inline);
        var item = new List<EpubElement>(); var builder = new InlineTextBuilder();
        RenderChildren(element, documentPath, item, builder, listDepth + 1, inheritedStyle); Flush(item, builder);
        bool firstText = true;
        foreach (EpubElement child in Clean(item))
        {
            if (child is EpubText text && text.Text.Length > 0)
            {
                string prefix = firstText ? $"{new string(' ', listDepth * 2)}• " : new string(' ', listDepth * 2 + 2);
                output.Add(Prefix(text, prefix)); firstText = false;
            }
            else output.Add(child);
        }
    }

    private static void RenderQuote(XElement element, string documentPath, List<EpubElement> output,
        InlineTextBuilder inline, int listDepth, EpubTextStyle inheritedStyle)
    {
        Flush(output, inline); var quote = new List<EpubElement>(); var builder = new InlineTextBuilder();
        RenderChildren(element, documentPath, quote, builder, listDepth, inheritedStyle | EpubTextStyle.Blockquote); Flush(quote, builder);
        foreach (EpubElement child in Clean(quote))
            output.Add(child is EpubText text ? Prefix(text, text.Text.Length == 0 ? "│" : "│ ") : child);
        Blank(output);
    }

    private static bool TryCreateDropCap(XElement paragraph, out EpubDropCap? dropCap)
    {
        dropCap = null;
        XElement? smallCap = paragraph.Descendants().FirstOrDefault(element =>
            (element.Attribute("class")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(value => value.Equals("smallcap", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("scap", StringComparison.OrdinalIgnoreCase)));
        if (smallCap?.Parent is not XElement lead) return false;

        EpubText styledParagraph = RenderInlineText(paragraph);
        string paragraphText = styledParagraph.Text;
        string leadText = Normalize(lead.Value);
        if (paragraphText.Length == 0 || leadText.Length == 0 ||
            !paragraphText.StartsWith(leadText, StringComparison.Ordinal)) return false;

        int initialIndex = 0;
        while (initialIndex < paragraphText.Length && !char.IsLetter(paragraphText[initialIndex])) initialIndex++;
        if (initialIndex > 2 || initialIndex >= paragraphText.Length) return false;
        string smallCapText = Normalize(smallCap.Value);
        if (smallCapText.Length == 0 || !paragraphText.AsSpan(initialIndex + 1).StartsWith(smallCapText, StringComparison.Ordinal))
            return false;

        dropCap = new EpubDropCap(paragraphText[..initialIndex], paragraphText[initialIndex],
            paragraphText[(initialIndex + 1)..], styledParagraph.Styles);
        return true;
    }

    private static EpubText RenderInlineText(XElement element)
    {
        var builder = new InlineTextBuilder();
        AppendInlineNodes(element, builder, EpubTextStyle.Normal);
        return builder.Take() ?? new EpubText("");
    }

    private static void AppendInlineNodes(XContainer parent, InlineTextBuilder builder, EpubTextStyle inheritedStyle)
    {
        foreach (XNode node in parent.Nodes())
        {
            if (node is XText text) { builder.Append(text.Value, inheritedStyle); continue; }
            if (node is not XElement element) continue;
            string name = element.Name.LocalName.ToLowerInvariant();
            if (name is "script" or "style" or "head" or "audio" or "video") continue;
            if (name == "br") { builder.Append(" ", inheritedStyle); continue; }
            EpubTextStyle childStyle = inheritedStyle | (name switch
            {
                "i" or "em" => EpubTextStyle.Emphasis,
                "b" or "strong" => EpubTextStyle.Strong,
                "code" or "kbd" or "samp" => EpubTextStyle.Code,
                "a" => EpubTextStyle.Link,
                _ => EpubTextStyle.Normal
            });
            AppendInlineNodes(element, builder, childStyle);
        }
    }

    private static EpubText StyledText(string text, EpubTextStyle style)
        => new(text, Enumerable.Repeat(style, text.Length).ToArray());

    private static EpubText Prefix(EpubText text, string prefix)
    {
        EpubTextStyle[] styles = new EpubTextStyle[prefix.Length + text.Text.Length];
        if (text.Styles is { Count: > 0 })
            for (int index = 0; index < text.Text.Length && index < text.Styles.Count; index++)
                styles[prefix.Length + index] = text.Styles[index];
        return new EpubText(prefix + text.Text, styles);
    }

    private static void Flush(List<EpubElement> output, InlineTextBuilder inline)
    {
        EpubText? text = inline.Take(); if (text is not null) output.Add(text);
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
                IReadOnlyList<EpubTextStyle>? styles = text.Styles is { } values
                    ? values.Take(value.Length).ToArray() : null;
                clean.Add(new EpubText(value, styles));
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
        return ApplyDialogueStyles(lines.Count == 0 ? [new EpubText("(This chapter contains no displayable content.)")] : lines);
    }

    private static IReadOnlyList<EpubElement> ApplyDialogueStyles(IReadOnlyList<EpubElement> elements)
    {
        bool doubleQuote = false, singleQuote = false, straightQuote = false, guillemet = false;
        var result = new List<EpubElement>(elements.Count);
        foreach (EpubElement element in elements)
        {
            string? text = element switch { EpubText value => value.Text, EpubDropCap value => value.Text, _ => null };
            if (text is null) { result.Add(element); continue; }
            EpubTextStyle[] styles = element switch
            {
                EpubText { Styles: { } values } => NormalizeStyles(values, text.Length),
                EpubDropCap { Styles: { } values } => NormalizeStyles(values, text.Length),
                _ => new EpubTextStyle[text.Length]
            };
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                bool straightBoundary = character == '"';
                if (character == '“') doubleQuote = true;
                else if (character == '‘') singleQuote = true;
                else if (character == '«') guillemet = true;
                else if (straightBoundary) straightQuote = !straightQuote;

                if (doubleQuote || singleQuote || straightQuote || guillemet || straightBoundary)
                    styles[index] |= EpubTextStyle.Dialogue;

                if (character == '”') { styles[index] |= EpubTextStyle.Dialogue; doubleQuote = false; }
                else if (character == '’' && singleQuote && !IsInternalApostrophe(text, index))
                { styles[index] |= EpubTextStyle.Dialogue; singleQuote = false; }
                else if (character == '»') { styles[index] |= EpubTextStyle.Dialogue; guillemet = false; }
            }
            result.Add(element switch
            {
                EpubText value => value with { Styles = styles },
                EpubDropCap value => value with { Styles = styles },
                _ => element
            });
        }
        return result;
    }

    private static bool IsInternalApostrophe(string text, int index)
        => index > 0 && index + 1 < text.Length && char.IsLetterOrDigit(text[index - 1]) && char.IsLetterOrDigit(text[index + 1]);

    private static EpubTextStyle[] NormalizeStyles(IReadOnlyList<EpubTextStyle> styles, int length)
    {
        var result = new EpubTextStyle[length];
        for (int index = 0; index < length && index < styles.Count; index++) result[index] = styles[index];
        return result;
    }

    private sealed class InlineTextBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly List<EpubTextStyle> _styles = [];

        public void Append(string value, EpubTextStyle style)
        {
            string normalized = Whitespace().Replace(WebUtility.HtmlDecode(value), " ");
            foreach (char character in normalized)
            {
                if (character == ' ' && (_text.Length == 0 || _text[^1] == ' ')) continue;
                _text.Append(character); _styles.Add(style);
            }
        }

        public EpubText? Take()
        {
            while (_text.Length > 0 && _text[^1] == ' ')
            { _text.Length--; _styles.RemoveAt(_styles.Count - 1); }
            if (_text.Length == 0) return null;
            var result = new EpubText(_text.ToString(), _styles.ToArray());
            _text.Clear(); _styles.Clear(); return result;
        }
    }

    private static string Normalize(string value) => Whitespace().Replace(WebUtility.HtmlDecode(value), " ").Trim();
    [GeneratedRegex(@"</?(?:p|div|section|article|aside|header|footer|main|nav|h[1-6]|li|blockquote|pre|br|hr|tr)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BreakTags();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)] private static partial Regex AnyTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
