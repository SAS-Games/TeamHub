using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace TeamHub.Web.Pages.Studio;

public static partial class ConfluenceUpdateHtml
{
    public static IHtmlContent Render(string? value, string emptyText)
    {
        var content = new HtmlContentBuilder();
        if (string.IsNullOrWhiteSpace(value)) return content.Append(emptyText);

        var offset = 0;
        foreach (Match match in LinkRegex().Matches(value))
        {
            content.Append(value[offset..match.Index]);

            var link = new TagBuilder("a");
            link.Attributes["href"] = match.Groups["url"].Value;
            link.Attributes["target"] = "_blank";
            link.Attributes["rel"] = "noopener noreferrer";
            link.InnerHtml.Append(match.Groups["label"].Value);
            content.AppendHtml(link);

            offset = match.Index + match.Length;
        }

        content.Append(value[offset..]);
        return content;
    }

    [GeneratedRegex(@"\[(?<label>[^\]\r\n]+)\]\((?<url>https?://[^\s)]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex LinkRegex();
}