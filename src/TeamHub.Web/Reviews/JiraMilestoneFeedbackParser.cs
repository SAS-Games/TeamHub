using System.Text.RegularExpressions;
using TeamHub.Studio;

namespace TeamHub.Web.Reviews;

internal static partial class JiraMilestoneFeedbackParser
{
    public static string[]? Parse(StudioJiraComment comment, string reviewerEmail)
    {
        if (comment.Parts.Any(part => part.Kind is not ("text" or "strong"))) return null;
        var text = string.Concat(comment.Parts.Select(part => part.Text)).Replace("\r\n", "\n");
        var markers = Marker().Matches(text);
        if (markers.Count != 1) return null;
        var marker = markers[0];
        if (marker.Index + marker.Length != text.Length
            || !Guid.TryParse(marker.Groups["id"].Value, out _)) return null;
        var email = Email().Matches(text);
        if (email.Count != 1 || !string.Equals(email[0].Groups["email"].Value.Trim(), reviewerEmail,
            StringComparison.OrdinalIgnoreCase)) return null;
        var sections = Sections().Matches(text);
        var names = new[] { "Session Details", "What's Good", "Needs Improvement", "Additional Feedback" };
        if (sections.Count != names.Length || !sections.Select(match => match.Groups["title"].Value).SequenceEqual(names)
            || marker.Index < sections[^1].Index) return null;
        var values = sections.Select((section, index) =>
        {
            var start = section.Index + section.Length;
            var end = index + 1 < sections.Count ? sections[index + 1].Index : marker.Index;
            return text[start..end].Trim();
        }).ToArray();
        return values.Any(value => value.Length > 20000) ? null : values;
    }

    [GeneratedRegex(@"^\[TeamHub Milestone Review Feedback: (?<id>[0-9a-fA-F-]{36})\]\s*$", RegexOptions.Multiline, 1000)]
    private static partial Regex Marker();
    [GeneratedRegex(@"^\*?Reviewer email\*?:[ \t]*(?<email>[^\n]+)$", RegexOptions.Multiline, 1000)]
    private static partial Regex Email();
    [GeneratedRegex(@"^\*?(?<title>Session Details|What's Good|Needs Improvement|Additional Feedback)\*?[ \t]*\n", RegexOptions.Multiline, 1000)]
    private static partial Regex Sections();
}
