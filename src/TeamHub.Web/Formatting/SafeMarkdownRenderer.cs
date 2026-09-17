using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace TeamHub.Web.Formatting;

public sealed partial class SafeMarkdownRenderer : ISafeMarkdownRenderer
{
    public string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var html = new StringBuilder();
        var paragraph = new List<string>();
        string? activeList = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(html, paragraph);
                CloseList(html, ref activeList);
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph(html, paragraph);
                CloseList(html, ref activeList);
                var level = heading.Groups[1].Value.Length;
                html.Append("<h").Append(level).Append('>')
                    .Append(RenderInline(heading.Groups[2].Value))
                    .Append("</h").Append(level).Append('>');
                continue;
            }

            var unordered = UnorderedListRegex().Match(line);
            if (unordered.Success)
            {
                AddListItem(html, paragraph, ref activeList, "ul", unordered.Groups[1].Value);
                continue;
            }

            var ordered = OrderedListRegex().Match(line);
            if (ordered.Success)
            {
                AddListItem(html, paragraph, ref activeList, "ol", ordered.Groups[1].Value);
                continue;
            }

            var quote = QuoteRegex().Match(line);
            if (quote.Success)
            {
                FlushParagraph(html, paragraph);
                CloseList(html, ref activeList);
                html.Append("<blockquote>").Append(RenderInline(quote.Groups[1].Value)).Append("</blockquote>");
                continue;
            }

            CloseList(html, ref activeList);
            paragraph.Add(line);
        }

        FlushParagraph(html, paragraph);
        CloseList(html, ref activeList);
        return html.ToString();
    }
    private static void FlushParagraph(StringBuilder html, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        html.Append("<p>");
        html.AppendJoin("<br />", paragraph.Select(RenderInline));
        html.Append("</p>");
        paragraph.Clear();
    }

    private static void CloseList(StringBuilder html, ref string? activeList)
    {
        if (activeList is null)
        {
            return;
        }

        html.Append("</").Append(activeList).Append('>');
        activeList = null;
    }

    private static void AddListItem(
        StringBuilder html,
        List<string> paragraph,
        ref string? activeList,
        string listName,
        string value)
    {
        FlushParagraph(html, paragraph);
        if (!string.Equals(activeList, listName, StringComparison.Ordinal))
        {
            CloseList(html, ref activeList);
            activeList = listName;
            html.Append('<').Append(listName).Append('>');
        }

        html.Append("<li>").Append(RenderInline(value)).Append("</li>");
    }

    private static string RenderInline(string source)
    {
        var protectedHtml = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokenPrefix = $"THMD{Guid.NewGuid():N}";
        var tokenIndex = 0;

        string Protect(string html)
        {
            var token = $"{tokenPrefix}{tokenIndex++}END";
            protectedHtml[token] = html;
            return token;
        }

        source = InlineCodeRegex().Replace(source, match =>
            Protect($"<code>{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</code>"));
        source = LinkRegex().Replace(source, match =>
        {
            var destination = match.Groups[2].Value;
            if (!IsSafeLink(destination))
            {
                return match.Value;
            }

            var label = HtmlEncoder.Default.Encode(match.Groups[1].Value);
            var href = HtmlEncoder.Default.Encode(destination);
            return Protect($"<a href='{href}' rel='noopener noreferrer'>{label}</a>");
        });

        var html = HtmlEncoder.Default.Encode(source);
        html = BoldRegex().Replace(html, "<strong>$1</strong>");
        html = HighlightRegex().Replace(html, "<mark>$1</mark>");
        html = StrikethroughRegex().Replace(html, "<del>$1</del>");
        html = ItalicRegex().Replace(html, "<em>$1</em>");

        foreach (var item in protectedHtml)
        {
            html = html.Replace(item.Key, item.Value, StringComparison.Ordinal);
        }

        return html;
    }

    private static bool IsSafeLink(string destination)
    {
        var decoded = WebUtility.HtmlDecode(destination);
        return Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.+?)\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*[-+*]\s+(.+?)\s*$")]
    private static partial Regex UnorderedListRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+(.+?)\s*$")]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"^\s*>\s?(.+?)\s*$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"\x60([^\x60\r\n]+)\x60")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"==(.+?)==")]
    private static partial Regex HighlightRegex();

    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex StrikethroughRegex();

    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)")]
    private static partial Regex ItalicRegex();
}
