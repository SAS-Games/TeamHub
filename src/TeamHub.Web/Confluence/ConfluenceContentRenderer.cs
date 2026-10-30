using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace TeamHub.Web.Confluence;

public interface IConfluenceContentRenderer
{
    string Render(
        string html,
        string confluenceBaseUrl,
        Guid confluenceTabId,
        IReadOnlyDictionary<string, string>? pageIdsByUrl = null);
    bool TryReadImageToken(string token, out string sourceUrl);
}

public sealed partial class ConfluenceContentRenderer : IConfluenceContentRenderer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "article", "aside", "b", "blockquote", "br", "caption", "code", "col", "colgroup",
        "dd", "del", "details", "div", "dl", "dt", "em", "figcaption", "figure", "h1", "h2", "h3", "h4",
        "h5", "h6", "hr", "i", "img", "li", "mark", "ol", "p", "pre", "s", "section", "small", "span",
        "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr", "u", "ul"
    };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "title", "lang", "dir"
    };

    private readonly ITimeLimitedDataProtector imageProtector;

    public ConfluenceContentRenderer(IDataProtectionProvider dataProtectionProvider)
    {
        imageProtector = dataProtectionProvider
            .CreateProtector("TeamHub.Confluence.Image.v1")
            .ToTimeLimitedDataProtector();
    }

    public string Render(
        string html,
        string confluenceBaseUrl,
        Guid confluenceTabId,
        IReadOnlyDictionary<string, string>? pageIdsByUrl = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var sanitized = InteractiveBlockRegex().Replace(html,
            "<div class=\"confluence-unsupported-content\">Interactive Confluence content is available from Open in Confluence.</div>");
        sanitized = DangerousBlockRegex().Replace(sanitized, string.Empty);
        sanitized = HtmlCommentRegex().Replace(sanitized, string.Empty);
        return HtmlTagRegex().Replace(sanitized, match =>
            SanitizeTag(match, confluenceBaseUrl, confluenceTabId, pageIdsByUrl));
    }

    public bool TryReadImageToken(string token, out string sourceUrl)
    {
        sourceUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            sourceUrl = imageProtector.Unprotect(token, out _);
            return !string.IsNullOrWhiteSpace(sourceUrl);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private string SanitizeTag(
        Match match,
        string confluenceBaseUrl,
        Guid confluenceTabId,
        IReadOnlyDictionary<string, string>? pageIdsByUrl)
    {
        var tag = match.Groups["tag"].Value.ToLowerInvariant();
        if (!AllowedTags.Contains(tag)) return string.Empty;
        if (match.Groups["closing"].Success) return IsVoidTag(tag) ? string.Empty : $"</{tag}>";

        var attributes = ParseAttributes(match.Groups["attributes"].Value);
        var output = new StringBuilder().Append('<').Append(tag);
        foreach (var attribute in attributes)
        {
            var name = attribute.Key.ToLowerInvariant();
            var value = attribute.Value;
            if (GlobalAttributes.Contains(name) || IsAriaAttribute(name) || IsAllowedTagAttribute(tag, name))
            {
                if (name is "href" or "src") continue;
                if (name == "class")
                {
                    value = SanitizeClassValue(value);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                }
                if (name == "dir" && value is not ("ltr" or "rtl" or "auto")) continue;
                output.Append(' ').Append(name).Append("=\"")
                    .Append(HtmlEncoder.Default.Encode(value)).Append('"');
            }
        }

        if (tag == "a" && attributes.TryGetValue("href", out var href)
            && TryRewriteLink(href, confluenceBaseUrl, confluenceTabId, pageIdsByUrl, out var safeHref, out var opensNewWindow))
        {
            output.Append(" href=\"").Append(HtmlEncoder.Default.Encode(safeHref)).Append('"');
            if (opensNewWindow) output.Append(" target=\"_blank\" rel=\"noopener noreferrer\"");
        }
        else if (tag == "img")
        {
            if (!attributes.TryGetValue("src", out var source)
                || !TryResolveSameOrigin(confluenceBaseUrl, source, out var imageUri)) return string.Empty;
            var token = imageProtector.Protect(imageUri.AbsoluteUri, TimeSpan.FromHours(8));
            output.Append(" src=\"/Confluence?handler=Image&amp;token=")
                .Append(HtmlEncoder.Default.Encode(token)).Append("\" loading=\"lazy\"");
        }

        output.Append('>');
        return output.ToString();
    }

    private static Dictionary<string, string> ParseAttributes(string source)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HtmlAttributeRegex().Matches(source))
        {
            var name = match.Groups["name"].Value;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)) continue;
            attributes.TryAdd(name, WebUtility.HtmlDecode(
                match.Groups["double"].Success ? match.Groups["double"].Value : match.Groups["single"].Value));
        }
        return attributes;
    }

    private static bool TryRewriteLink(
        string href,
        string confluenceBaseUrl,
        Guid confluenceTabId,
        IReadOnlyDictionary<string, string>? pageIdsByUrl,
        out string safeHref,
        out bool opensNewWindow)
    {
        safeHref = string.Empty;
        opensNewWindow = false;
        var decoded = WebUtility.HtmlDecode(href).Trim();
        if (decoded.StartsWith('#'))
        {
            safeHref = decoded;
            return true;
        }
        if (decoded.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            safeHref = decoded;
            opensNewWindow = true;
            return true;
        }
        if (!Uri.TryCreate(confluenceBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || !Uri.TryCreate(baseUri, decoded, out var uri)
            || uri.Scheme is not ("http" or "https")) return false;

        if (IsSameOrigin(baseUri, uri))
        {
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (query.TryGetValue("pageId", out var values)
                && values.Count > 0 && !string.IsNullOrWhiteSpace(values[0]))
            {
                safeHref = "/Confluence?tabId=" + Uri.EscapeDataString(confluenceTabId.ToString("D"))
                    + "&pageId=" + Uri.EscapeDataString(values[0]!);
                return true;
            }
            if (pageIdsByUrl is not null)
            {
                var location = NormalizePageLocation(uri);
                var matchingPage = pageIdsByUrl.FirstOrDefault(item =>
                    Uri.TryCreate(item.Key, UriKind.Absolute, out var configuredUri)
                    && string.Equals(location, NormalizePageLocation(configuredUri), StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(matchingPage.Value))
                {
                    safeHref = "/Confluence?tabId=" + Uri.EscapeDataString(confluenceTabId.ToString("D"))
                        + "&pageId=" + Uri.EscapeDataString(matchingPage.Value);
                    return true;
                }
            }
        }
        safeHref = uri.AbsoluteUri;
        opensNewWindow = true;
        return true;
    }

    private static bool TryResolveSameOrigin(string baseUrl, string source, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || !Uri.TryCreate(baseUri, WebUtility.HtmlDecode(source), out var candidate)
            || !IsSameOrigin(baseUri, candidate)
            || !baseUri.IsBaseOf(candidate)) return false;
        uri = candidate;
        return true;
    }

    private static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static string NormalizePageLocation(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Replace('+', ' ').TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    private static bool IsAllowedTagAttribute(string tag, string name) => (tag, name) switch
    {
        ("a", "href") => true,
        ("img", "src" or "alt" or "width" or "height") => true,
        ("td" or "th", "colspan" or "rowspan" or "scope" or "headers") => true,
        ("col" or "colgroup", "span") => true,
        ("ol", "start" or "type" or "reversed") => true,
        ("li", "value") => true,
        ("details", "open") => true,
        ("time", "datetime") => true,
        _ => false
    };

    private static bool IsAriaAttribute(string name) => name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase);
    private static bool IsVoidTag(string tag) => tag is "br" or "col" or "hr" or "img";

    private static string SanitizeClassValue(string value) => string.Join(' ', value
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => SafeClassNameRegex().IsMatch(item))
        .Where(item => item.StartsWith("confluence-", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("content", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("column", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("panel", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("code", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("status-", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("aui-", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("jira-", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("image", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("emoticon", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("expand", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("highlight", StringComparison.OrdinalIgnoreCase)
            || item.Equals("cell", StringComparison.OrdinalIgnoreCase)
            || item.Equals("external-link", StringComparison.OrdinalIgnoreCase)));

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, 2000)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"<(?:iframe|object|embed)\b[^>]*>.*?</(?:iframe|object|embed)>|<(?:iframe|object|embed)\b[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex InteractiveBlockRegex();

    [GeneratedRegex(@"<(?:script|style|form|button|input|select|textarea|meta|link|base)\b[^>]*>.*?</(?:script|style|form|button|select|textarea)>|<(?:script|style|form|button|input|select|textarea|meta|link|base)\b[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex DangerousBlockRegex();

    [GeneratedRegex(@"<(?<closing>/)?(?<tag>[A-Za-z][A-Za-z0-9:-]*)(?<attributes>[^>]*)>", RegexOptions.Singleline, 2000)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("(?<name>[A-Za-z_:][A-Za-z0-9_:.-]*)\\s*=\\s*(?:\\\"(?<double>[^\\\"]*)\\\"|'(?<single>[^']*)')", RegexOptions.Singleline, 2000)]
    private static partial Regex HtmlAttributeRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,100}$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex SafeClassNameRegex();
}
