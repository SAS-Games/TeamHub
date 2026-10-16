using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamHub.Studio;

internal static partial class JiraCommentMarkdown
{
    public static string ToJiraWiki(string markdown)
    {
        var lines = Normalize(markdown).Split('\n');
        var output = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var heading = MarkdownHeading().Match(line);
            if (heading.Success)
            {
                output.Add($"h{heading.Groups[1].Value.Length}. {ToJiraInline(heading.Groups[2].Value)}");
                continue;
            }

            var unordered = MarkdownUnorderedList().Match(line);
            if (unordered.Success)
            {
                output.Add($"* {ToJiraInline(unordered.Groups[1].Value)}");
                continue;
            }

            var ordered = MarkdownOrderedList().Match(line);
            if (ordered.Success)
            {
                output.Add($"# {ToJiraInline(ordered.Groups[1].Value)}");
                continue;
            }

            var quote = MarkdownQuote().Match(line);
            output.Add(quote.Success
                ? $"bq. {ToJiraInline(quote.Groups[1].Value)}"
                : ToJiraInline(line));
        }
        return string.Join('\n', output);
    }

    public static string FromJira(JsonElement body, IReadOnlyList<JiraCommentAttachment> attachments)
    {
        if (body.ValueKind == JsonValueKind.String)
            return FromJiraWiki(body.GetString() ?? string.Empty);

        var parts = JiraCommentBodyParser.Parse(body, attachments);
        var output = new StringBuilder();
        foreach (var part in parts)
        {
            output.Append(part.Kind switch
            {
                "strong" => $"**{part.Text}**",
                "link" when part.Url is not null => $"[{part.Text}]({part.Url})",
                "image" => string.Empty,
                _ => part.Text
            });
        }
        return NormalizeBlankLines(output.ToString());
    }

    private static string FromJiraWiki(string value)
    {
        var lines = Normalize(value).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var heading = JiraHeading().Match(line);
            if (heading.Success)
                line = $"{new string('#', int.Parse(heading.Groups[1].Value))} {heading.Groups[2].Value}";
            else if (JiraUnorderedList().IsMatch(line))
                line = JiraUnorderedList().Replace(line, "- $1");
            else if (JiraOrderedList().IsMatch(line))
                line = JiraOrderedList().Replace(line, "1. $1");
            else if (line.StartsWith("bq. ", StringComparison.Ordinal))
                line = $"> {line[4..]}";

            lines[index] = FromJiraInline(line);
        }
        return NormalizeBlankLines(string.Join('\n', lines));
    }

    private static string ToJiraInline(string value)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = $"THJIRA{Guid.NewGuid():N}";
        var next = 0;
        string Protect(string replacement)
        {
            var token = $"{prefix}{next++}END";
            replacements[token] = replacement;
            return token;
        }

        value = MarkdownCode().Replace(value, match => Protect($"{{{{{match.Groups[1].Value}}}}}"));
        value = MarkdownLink().Replace(value, match =>
            IsSafeUrl(match.Groups[2].Value)
                ? Protect($"[{match.Groups[1].Value}|{match.Groups[2].Value}]")
                : match.Value);
        value = MarkdownBold().Replace(value, match => Protect($"*{match.Groups[1].Value}*"));
        value = MarkdownHighlight().Replace(value, match => Protect($"*{match.Groups[1].Value}*"));
        value = MarkdownStrike().Replace(value, match => Protect($"-{match.Groups[1].Value}-"));
        value = MarkdownItalic().Replace(value, match => Protect($"_{match.Groups[1].Value}_"));
        foreach (var replacement in replacements)
            value = value.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        return value;
    }

    private static string FromJiraInline(string value)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = $"THMARKDOWN{Guid.NewGuid():N}";
        var next = 0;
        string Protect(string replacement)
        {
            var token = $"{prefix}{next++}END";
            replacements[token] = replacement;
            return token;
        }

        value = JiraImage().Replace(value, string.Empty);
        value = JiraLink().Replace(value, match =>
            IsSafeUrl(match.Groups[2].Value)
                ? Protect($"[{match.Groups[1].Value}]({match.Groups[2].Value})")
                : match.Value);
        value = JiraCode().Replace(value, match => Protect($"`{match.Groups[1].Value}`"));
        value = JiraBold().Replace(value, match => Protect($"**{match.Groups[1].Value}**"));
        value = JiraItalic().Replace(value, match => Protect($"*{match.Groups[1].Value}*"));
        value = JiraStrike().Replace(value, match => Protect($"~~{match.Groups[1].Value}~~"));
        foreach (var replacement in replacements)
            value = value.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        return value;
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizeBlankLines(string value) =>
        ExcessBlankLines().Replace(Normalize(value).Trim(), "\n\n");

    private static bool IsSafeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.UserInfo.Length == 0
        && uri.Scheme is "http" or "https";

    [GeneratedRegex(@"^(#{1,3})\s+(.+?)\s*$")]
    private static partial Regex MarkdownHeading();
    [GeneratedRegex(@"^\s*[-+*]\s+(.+?)\s*$")]
    private static partial Regex MarkdownUnorderedList();
    [GeneratedRegex(@"^\s*\d+\.\s+(.+?)\s*$")]
    private static partial Regex MarkdownOrderedList();
    [GeneratedRegex(@"^\s*>\s?(.+?)\s*$")]
    private static partial Regex MarkdownQuote();
    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex MarkdownCode();
    [GeneratedRegex(@"\[([^\]\r\n]+)\]\((https?://[^)\s]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLink();
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex MarkdownBold();
    [GeneratedRegex(@"==(.+?)==")]
    private static partial Regex MarkdownHighlight();
    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex MarkdownStrike();
    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)")]
    private static partial Regex MarkdownItalic();

    [GeneratedRegex(@"^h([1-3])\.\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex JiraHeading();
    [GeneratedRegex(@"^\s*\*\s+(.+?)\s*$")]
    private static partial Regex JiraUnorderedList();
    [GeneratedRegex(@"^\s*#\s+(.+?)\s*$")]
    private static partial Regex JiraOrderedList();
    [GeneratedRegex(@"!([^!\r\n]+)!")]
    private static partial Regex JiraImage();
    [GeneratedRegex(@"\[([^\]|\r\n]+)\|(https?://[^\]\s]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex JiraLink();
    [GeneratedRegex(@"\{\{([^}\r\n]+)\}\}")]
    private static partial Regex JiraCode();
    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)")]
    private static partial Regex JiraBold();
    [GeneratedRegex(@"(?<!_)_([^_\r\n]+)_(?!_)")]
    private static partial Regex JiraItalic();
    [GeneratedRegex(@"(?<!-)-([^\-\r\n]+)-(?!-)")]
    private static partial Regex JiraStrike();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();
}
