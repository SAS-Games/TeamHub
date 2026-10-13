using System.Text;

namespace TeamHub.Excel;

public static class MicrosoftCloudLinkHelper
{
    private static readonly byte[] OpenXmlSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OpenXmlEmptySignature = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] OpenXmlSpannedSignature = [0x50, 0x4B, 0x07, 0x08];

    public static bool IsSupportedExcelStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length - stream.Position < 4) return false;
        var position = stream.Position;
        Span<byte> header = stackalloc byte[4];
        var read = stream.Read(header);
        stream.Position = position;
        if (read < 4) return false;

        if (header.SequenceEqual(OpenXmlSignature)
            || header.SequenceEqual(OpenXmlEmptySignature)
            || header.SequenceEqual(OpenXmlSpannedSignature))
        {
            return true;
        }

        return LegacyExcelWorkbook.IsLegacyBinary(stream);
    }

    public static bool IsMicrosoftCloudUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host;
        return host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".sharepoint.cn", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".sharepoint-mil.us", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".1drv.ms", StringComparison.OrdinalIgnoreCase)
            || host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".onedrive.live.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetDirectDownloadCandidateUrls(string inputUrl)
    {
        if (string.IsNullOrWhiteSpace(inputUrl) || !Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri))
            return string.IsNullOrWhiteSpace(inputUrl) ? [] : [inputUrl];

        var candidates = new List<string>();
        var path = uri.AbsolutePath;
        var query = uri.Query;

        // 1. SharePoint Doc.aspx or WopiFrame.aspx with sourcedoc={GUID}
        var docIndex = path.IndexOf("/_layouts/15/Doc.aspx", StringComparison.OrdinalIgnoreCase);
        if (docIndex < 0)
        {
            docIndex = path.IndexOf("/_layouts/15/WopiFrame.aspx", StringComparison.OrdinalIgnoreCase);
        }

        if (docIndex >= 0)
        {
            var sourcedoc = ExtractQueryParam(query, "sourcedoc");
            if (!string.IsNullOrWhiteSpace(sourcedoc))
            {
                var cleanGuid = sourcedoc.Trim().Trim('{', '}');
                var basePath = path[..docIndex];
                var baseUri = $"{uri.Scheme}://{uri.Authority}{basePath}";

                candidates.Add($"{baseUri}/_layouts/15/download.aspx?sourcedoc={Uri.EscapeDataString(sourcedoc)}");
                candidates.Add($"{baseUri}/_layouts/15/download.aspx?UniqueId={cleanGuid}");

                var actionDownloadQuery = SetQueryParam(query, "action", "download");
                candidates.Add($"{uri.Scheme}://{uri.Authority}{path}{actionDownloadQuery}");
            }
        }

        // 2. SharePoint documents / sharing links (:x:, :w:, :p:, /r/, /s/, /g/, etc.)
        if (uri.Host.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/download.aspx", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(inputUrl);
            }
            else
            {
                var withDownload = SetQueryParam(query, "download", "1");
                withDownload = RemoveQueryParam(withDownload, "web");
                candidates.Add($"{uri.Scheme}://{uri.Authority}{path}{withDownload}");
            }
        }

        // 3. OneDrive personal links (1drv.ms / onedrive.live.com)
        if (uri.Host.Contains("1drv.ms", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(SetQueryParam(inputUrl, "download", "1"));
        }
        else if (uri.Host.Contains("onedrive.live.com", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/view.aspx", StringComparison.OrdinalIgnoreCase))
            {
                var downloadPath = path.Replace("/view.aspx", "/download.aspx", StringComparison.OrdinalIgnoreCase);
                candidates.Add($"{uri.Scheme}://{uri.Authority}{downloadPath}{query}");
            }
            if (path.Contains("/redir", StringComparison.OrdinalIgnoreCase))
            {
                var downloadPath = path.Replace("/redir", "/download", StringComparison.OrdinalIgnoreCase);
                candidates.Add($"{uri.Scheme}://{uri.Authority}{downloadPath}{query}");
            }
            candidates.Add($"{uri.Scheme}://{uri.Authority}{path}{SetQueryParam(query, "download", "1")}");
        }

        if (!candidates.Contains(inputUrl, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(inputUrl);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? ExtractQueryParam(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length > 0 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            }
        }
        return null;
    }

    public static string SetQueryParam(string query, string name, string value)
    {
        var trimmed = (query ?? string.Empty).TrimStart('?');
        var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => !string.Equals(Uri.UnescapeDataString(p[0]), name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Length > 1 ? $"{p[0]}={p[1]}" : p[0])
            .ToList();
        pairs.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        return "?" + string.Join("&", pairs);
    }

    public static string RemoveQueryParam(string query, string name)
    {
        var trimmed = (query ?? string.Empty).TrimStart('?');
        var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => !string.Equals(Uri.UnescapeDataString(p[0]), name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Length > 1 ? $"{p[0]}={p[1]}" : p[0])
            .ToList();
        return pairs.Count > 0 ? "?" + string.Join("&", pairs) : string.Empty;
    }
}
