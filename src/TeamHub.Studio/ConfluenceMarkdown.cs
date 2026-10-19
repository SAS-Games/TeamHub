using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace TeamHub.Studio;

internal static partial class ConfluenceMarkdown
{
    public static string ToStorage(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new StringBuilder();
        var paragraph = new List<string>();
        string? activeList = null;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            output.Append("<p>").AppendJoin("<br/>", paragraph.Select(ToStorageInline)).Append("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (activeList is null) return;
            output.Append("</").Append(activeList).Append('>');
            activeList = null;
        }

        void AddListItem(string listName, string value)
        {
            FlushParagraph();
            if (!string.Equals(activeList, listName, StringComparison.Ordinal))
            {
                CloseList();
                activeList = listName;
                output.Append('<').Append(listName).Append('>');
            }
            output.Append("<li>").Append(ToStorageInline(value)).Append("</li>");
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                CloseList();
                var level = heading.Groups[1].Value.Length;
                output.Append("<h").Append(level).Append('>')
                    .Append(ToStorageInline(heading.Groups[2].Value))
                    .Append("</h").Append(level).Append('>');
                continue;
            }

            var unordered = UnorderedList().Match(line);
            if (unordered.Success)
            {
                AddListItem("ul", unordered.Groups[1].Value);
                continue;
            }

            var ordered = OrderedList().Match(line);
            if (ordered.Success)
            {
                AddListItem("ol", ordered.Groups[1].Value);
                continue;
            }

            var quote = Quote().Match(line);
            if (quote.Success)
            {
                FlushParagraph();
                CloseList();
                output.Append("<blockquote><p>").Append(ToStorageInline(quote.Groups[1].Value))
                    .Append("</p></blockquote>");
                continue;
            }

            CloseList();
            paragraph.Add(line);
        }

        FlushParagraph();
        CloseList();
        return output.ToString();
    }

    public static string PreserveStorageFormatting(string html)
    {
        html = SectionLabel().Replace(html, match => $"{match.Groups[1].Value}:");
        html = StrongEmphasis().Replace(html, match => $"***{match.Groups[1].Value}***");
        html = EmphasisStrong().Replace(html, match => $"***{match.Groups[1].Value}***");
        html = Strong().Replace(html, match => $"**{match.Groups[1].Value}**");
        html = Emphasis().Replace(html, match => $"*{match.Groups[1].Value}*");
        html = Deleted().Replace(html, match => $"~~{match.Groups[1].Value}~~");
        html = Highlighted().Replace(html, match => $"=={match.Groups[1].Value}==");
        html = Code().Replace(html, match => $"`{match.Groups[1].Value}`");
        html = HeadingElement().Replace(html, match =>
            $"\n{new string('#', int.Parse(match.Groups[1].Value))} {match.Groups[2].Value}\n");
        html = ConvertListsToMarkdown(html);
        html = BlockquoteElement().Replace(html, match =>
            $"\n> {match.Groups[1].Value.Trim()}\n");
        return html;
    }

    private static string ConvertListsToMarkdown(string html)
    {
        var output = new StringBuilder();
        var position = 0;
        while (true)
        {
            var start = ListElementStart().Match(html, position);
            if (!start.Success)
            {
                output.Append(html, position, html.Length - position);
                return output.ToString();
            }

            output.Append(html, position, start.Index - position);
            if (!TryRenderList(html, start, 0, out var markdown, out var end))
            {
                output.Append(start.Value);
                position = start.Index + start.Length;
                continue;
            }

            output.Append('\n').Append(markdown).Append('\n');
            position = end;
        }
    }

    private static bool TryRenderList(
        string source,
        Match start,
        int nestingLevel,
        out string markdown,
        out int end)
    {
        markdown = string.Empty;
        end = start.Index;
        if (!TryReadElement(source, start, ListElementTag(), out var content, out end)) return false;

        var ordered = string.Equals(start.Groups["tag"].Value, "ol", StringComparison.OrdinalIgnoreCase);
        var output = new StringBuilder();
        var position = 0;
        var itemNumber = 0;
        while (true)
        {
            var itemStart = ListItemTag().Match(content, position);
            var siblingNestedStart = ListElementStart().Match(content, position);
            if (siblingNestedStart.Success && (!itemStart.Success || siblingNestedStart.Index < itemStart.Index))
            {
                if (!TryRenderList(content, siblingNestedStart, nestingLevel + 1, out var nestedMarkdown, out var nestedEnd)) return false;
                output.Append(nestedMarkdown).Append('\n');
                position = nestedEnd;
                continue;
            }
            if (!itemStart.Success) break;
            if (itemStart.Groups["closing"].Success)
            {
                position = itemStart.Index + itemStart.Length;
                continue;
            }
            if (!TryReadElement(content, itemStart, ListItemTag(), out var itemContent, out var itemEnd)) return false;

            var itemText = new StringBuilder();
            var nestedPosition = 0;
            while (true)
            {
                var nestedStart = ListElementStart().Match(itemContent, nestedPosition);
                if (!nestedStart.Success)
                {
                    itemText.Append(itemContent, nestedPosition, itemContent.Length - nestedPosition);
                    break;
                }

                itemText.Append(itemContent, nestedPosition, nestedStart.Index - nestedPosition);
                if (!TryRenderList(itemContent, nestedStart, nestingLevel + 1, out var nestedMarkdown, out var nestedEnd))
                {
                    itemText.Append(nestedStart.Value);
                    nestedPosition = nestedStart.Index + nestedStart.Length;
                    continue;
                }

                itemText.Append('\n').Append(nestedMarkdown);
                nestedPosition = nestedEnd;
            }

            var lines = itemText.ToString().Split('\n');
            var firstLine = NormalizeListItemText(lines[0]);
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                output.Append(' ', nestingLevel * 2)
                    .Append(ordered ? $"{++itemNumber}. " : "- ")
                    .Append(firstLine)
                    .Append('\n');
            }
            foreach (var line in lines.Skip(1))
            {
                if (!string.IsNullOrWhiteSpace(line)) output.Append(line.TrimEnd()).Append('\n');
            }

            position = itemEnd;
        }

        markdown = output.ToString().TrimEnd();
        return true;
    }

    private static bool TryReadElement(
        string source,
        Match start,
        Regex tagMatcher,
        out string content,
        out int end)
    {
        content = string.Empty;
        end = start.Index;
        var depth = 1;
        var position = start.Index + start.Length;
        while (true)
        {
            var tag = tagMatcher.Match(source, position);
            if (!tag.Success) return false;

            if (tag.Groups["closing"].Success)
            {
                if (--depth == 0)
                {
                    content = source[position..tag.Index];
                    end = tag.Index + tag.Length;
                    return true;
                }
            }
            else
            {
                depth++;
            }
            position = tag.Index + tag.Length;
        }
    }

    private static string NormalizeListItemText(string html) =>
        ListItemBlockElement().Replace(ListItemBreak().Replace(html, " "), " ").Trim();

    private static string ToStorageInline(string source)
    {
        var protectedHtml = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = $"THCONF{Guid.NewGuid():N}";
        var next = 0;
        string Protect(string html)
        {
            var token = $"{prefix}{next++}END";
            protectedHtml[token] = html;
            return token;
        }

        source = InlineCode().Replace(source, match =>
            Protect($"<code>{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</code>"));
        source = Link().Replace(source, match =>
        {
            var destination = match.Groups[2].Value;
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)
                || uri.UserInfo.Length != 0 || uri.Scheme is not ("http" or "https")) return match.Value;
            return Protect($"<a href=\"{HtmlEncoder.Default.Encode(uri.AbsoluteUri)}\">{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</a>");
        });
        source = BoldItalic().Replace(source, match =>
            Protect($"<strong><em>{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</em></strong>"));
        source = Bold().Replace(source, match => Protect($"<strong>{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</strong>"));
        source = Highlight().Replace(source, match => Protect($"<mark>{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</mark>"));
        source = Strike().Replace(source, match => Protect($"<del>{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</del>"));
        source = Italic().Replace(source, match => Protect($"<em>{HtmlEncoder.Default.Encode(match.Groups[1].Value)}</em>"));
        var encoded = HtmlEncoder.Default.Encode(source);
        foreach (var item in protectedHtml)
            encoded = encoded.Replace(item.Key, item.Value, StringComparison.Ordinal);
        return encoded;
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.+?)\s*$")]
    private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-+*]\s+(.+?)\s*$")]
    private static partial Regex UnorderedList();
    [GeneratedRegex(@"^\s*\d+\.\s+(.+?)\s*$")]
    private static partial Regex OrderedList();
    [GeneratedRegex(@"^\s*>\s?(.+?)\s*$")]
    private static partial Regex Quote();
    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex InlineCode();
    [GeneratedRegex(@"\[([^\]\r\n]+)\]\((https?://[^)\s]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
    [GeneratedRegex(@"\*\*\*(.+?)\*\*\*")]
    private static partial Regex BoldItalic();
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
    [GeneratedRegex(@"==(.+?)==")]
    private static partial Regex Highlight();
    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex Strike();
    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)")]
    private static partial Regex Italic();

    [GeneratedRegex(@"<strong\b[^>]*>\s*(Studio Work|HPGDS Support|WMD Support|Action Items?)\s*:\s*</strong>", RegexOptions.IgnoreCase)]
    private static partial Regex SectionLabel();
    [GeneratedRegex(@"<strong\b[^>]*>\s*<em\b[^>]*>(.*?)</em>\s*</strong>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StrongEmphasis();
    [GeneratedRegex(@"<em\b[^>]*>\s*<strong\b[^>]*>(.*?)</strong>\s*</em>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EmphasisStrong();
    [GeneratedRegex(@"<strong\b[^>]*>(.*?)</strong>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Strong();
    [GeneratedRegex(@"<em\b[^>]*>(.*?)</em>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Emphasis();
    [GeneratedRegex(@"<(?:del|s)\b[^>]*>(.*?)</(?:del|s)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Deleted();
    [GeneratedRegex(@"<mark\b[^>]*>(.*?)</mark>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Highlighted();
    [GeneratedRegex(@"<code\b[^>]*>(.*?)</code>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Code();
    [GeneratedRegex(@"<h([1-3])\b[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingElement();
    [GeneratedRegex(@"<(?<closing>/)?(?<tag>ul|ol)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListElementTag();
    [GeneratedRegex(@"<(?<tag>ul|ol)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListElementStart();
    [GeneratedRegex(@"<(?<closing>/)?li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListItemTag();
    [GeneratedRegex(@"<br\b[^>]*?/?>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemBreak();
    [GeneratedRegex(@"</?(?:p|div)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListItemBlockElement();
    [GeneratedRegex(@"<blockquote\b[^>]*>(.*?)</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockquoteElement();
}
