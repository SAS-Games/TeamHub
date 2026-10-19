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
        var activeLists = new Stack<ListState>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(html, paragraph);
                CloseLists(html, activeLists);
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph(html, paragraph);
                CloseLists(html, activeLists);
                var level = heading.Groups[1].Value.Length;
                html.Append("<h").Append(level).Append('>')
                    .Append(RenderInline(heading.Groups[2].Value))
                    .Append("</h").Append(level).Append('>');
                continue;
            }

            var listItem = ListItemRegex().Match(line);
            if (listItem.Success)
            {
                FlushParagraph(html, paragraph);
                var marker = listItem.Groups[2].Value;
                var listName = marker.EndsWith(".", StringComparison.Ordinal) ? "ol" : "ul";
                var indentation = listItem.Groups[1].Value.Replace("\t", "  ", StringComparison.Ordinal).Length;
                AddListItem(html, activeLists, indentation, listName, listItem.Groups[3].Value);
                continue;
            }

            var quote = QuoteRegex().Match(line);
            if (quote.Success)
            {
                FlushParagraph(html, paragraph);
                CloseLists(html, activeLists);
                html.Append("<blockquote>").Append(RenderInline(quote.Groups[1].Value)).Append("</blockquote>");
                continue;
            }

            CloseLists(html, activeLists);
            paragraph.Add(line);
        }

        FlushParagraph(html, paragraph);
        CloseLists(html, activeLists);
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

    private static void CloseLists(StringBuilder html, Stack<ListState> activeLists)
    {
        while (activeLists.Count > 0)
        {
            CloseList(html, activeLists.Pop());
        }
    }

    private static void AddListItem(
        StringBuilder html,
        Stack<ListState> activeLists,
        int indentation,
        string listName,
        string value)
    {
        while (activeLists.Count > 0 && indentation < activeLists.Peek().Indentation)
        {
            CloseList(html, activeLists.Pop());
        }

        if (activeLists.Count > 0 && indentation == activeLists.Peek().Indentation
            && string.Equals(listName, activeLists.Peek().Name, StringComparison.Ordinal))
        {
            html.Append("</li>");
            html.Append("<li>").Append(RenderInline(value));
            return;
        }

        if (activeLists.Count > 0 && indentation == activeLists.Peek().Indentation)
        {
            CloseList(html, activeLists.Pop());
        }

        html.Append('<').Append(listName).Append('>');
        html.Append("<li>").Append(RenderInline(value));
        activeLists.Push(new ListState(indentation, listName));
    }

    private static void CloseList(StringBuilder html, ListState list)
    {
        html.Append("</li></").Append(list.Name).Append('>');
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
        html = BoldItalicRegex().Replace(html, "<strong><em>$1</em></strong>");
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

    [GeneratedRegex(@"^(\s*)([-+*]|\d+\.)\s+(.+?)\s*$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\s*>\s?(.+?)\s*$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"\x60([^\x60\r\n]+)\x60")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*\*(.+?)\*\*\*")]
    private static partial Regex BoldItalicRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"==(.+?)==")]
    private static partial Regex HighlightRegex();

    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex StrikethroughRegex();

    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)")]
    private static partial Regex ItalicRegex();

    private sealed record ListState(int Indentation, string Name);
}
