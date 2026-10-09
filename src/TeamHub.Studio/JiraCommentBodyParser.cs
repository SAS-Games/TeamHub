using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamHub.Studio;

internal static partial class JiraCommentBodyParser
{
    public static IReadOnlyList<StudioJiraCommentPart> Parse(
        JsonElement body, IReadOnlyList<JiraCommentAttachment> attachments)
    {
        var parts = new List<StudioJiraCommentPart>();
        if (body.ValueKind == JsonValueKind.String)
        {
            var text = body.GetString() ?? "";
            var position = 0;
            foreach (Match match in WikiMedia().Matches(text))
            {
                if (match.Index > position) parts.Add(new("text", text[position..match.Index]));
                if (match.Groups["image"].Success)
                    AddImage(parts, match.Groups["image"].Value.Split('|')[0].Trim(), attachments);
                else
                    parts.Add(SafeUrl(match.Groups["url"].Value)
                        ? new("link", match.Groups["label"].Value, match.Groups["url"].Value)
                        : new("text", match.Value));
                position = match.Index + match.Length;
            }
            if (position < text.Length) parts.Add(new("text", text[position..]));
        }
        else if (body.ValueKind == JsonValueKind.Object)
        {
            ReadDocumentNode(body, parts, attachments);
        }
        else
        {
            throw new StudioJiraCommentException("Jira returned an unsupported comment body format.");
        }
        return parts;
    }

    private static void ReadDocumentNode(JsonElement node, List<StudioJiraCommentPart> parts,
        IReadOnlyList<JiraCommentAttachment> attachments)
    {
        var type = node.GetProperty("type").GetString();
        var attrs = node.TryGetProperty("attrs", out var attributes) ? attributes : default;
        switch (type)
        {
            case "text":
                var text = node.GetProperty("text").GetString() ?? "";
                var link = node.TryGetProperty("marks", out var marks)
                    ? marks.EnumerateArray().FirstOrDefault(mark => mark.GetProperty("type").GetString() == "link")
                    : default;
                var href = link.ValueKind == JsonValueKind.Object
                    ? link.GetProperty("attrs").GetProperty("href").GetString() : null;
                parts.Add(SafeUrl(href) ? new("link", text, href) : new("text", text));
                return;
            case "hardBreak":
                parts.Add(new("text", "\n"));
                return;
            case "mention":
            case "emoji":
                parts.Add(new("text", Attribute(attrs, "text") ?? Attribute(attrs, "shortName") ?? "[Mention]"));
                return;
            case "inlineCard":
                var url = Attribute(attrs, "url");
                parts.Add(SafeUrl(url) ? new("link", url!, url) : new("text", "[Link unavailable in preview]"));
                return;
            case "media":
            case "mediaInline":
                AddImage(parts, Attribute(attrs, "url") ?? Attribute(attrs, "id") ?? Attribute(attrs, "alt") ?? "", attachments);
                return;
        }
        var hasContent = node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array;
        if (hasContent)
            foreach (var child in content.EnumerateArray()) ReadDocumentNode(child, parts, attachments);
        else if (type != "doc")
            parts.Add(new("text", "[Content unavailable in preview; open this comment in Jira.]"));
        if (type is "paragraph" or "listItem" or "heading" or "codeBlock" or "blockquote" or "tableRow")
            parts.Add(new("text", "\n"));
    }

    private static void AddImage(List<StudioJiraCommentPart> parts, string reference,
        IReadOnlyList<JiraCommentAttachment> attachments)
    {
        var matches = attachments.Where(item =>
            string.Equals(item.FileName, reference, StringComparison.Ordinal)
            || item.Id == reference || item.ContentUrl == reference).ToList();
        if (matches.Count == 1 && matches[0].ContentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp")
        {
            parts.Add(new("image", matches[0].FileName, AttachmentId: matches[0].Id));
            return;
        }
        if (SafeUrl(reference))
            parts.Add(new("link", $"Image: {reference}", reference));
        else
            parts.Add(new("text", $"[Image: {reference}. Unavailable in preview; open this comment in Jira.]"));
    }

    private static string? Attribute(JsonElement attrs, string name) =>
        attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0;

    [GeneratedRegex(@"!(?<image>[^!\r\n]+)!|\[(?<label>[^\]\r\n]+)\|(?<url>https?://[^\]\s]+)\]",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WikiMedia();
}
