using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TeamHub.Excel;

public static class ExcelWorkbookSourceTypes
{
    public const string LocalFile = "LocalFile";
    public const string ExcelUrl = "ExcelUrl";
    public const string UploadedExcel = "UploadedExcel";
    public const string MicrosoftGraphExcel = "MicrosoftGraphExcel";
}

public static class ExcelWorkbookLimits
{
    public const int MaximumWorkbookBytes = 25 * 1024 * 1024;
}

public static class ExcelWorkbookSourceValidation
{
    public static string ValidateMicrosoftSharingUrl(string? sharingUrl)
    {
        var normalized = sharingUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Enter a valid HTTPS SharePoint or OneDrive workbook link.", nameof(sharingUrl));
        return normalized;
    }
}

public sealed record ExcelWorkbookSourceRequest(
    string SourceType,
    string? LocalPath = null,
    string? SourceUrl = null,
    string? ManagedReference = null,
    string? DriveId = null,
    string? ItemId = null,
    string? SharingUrl = null);

public interface IExcelWorkbookSource
{
    Task<MemoryStream> OpenAsync(
        ExcelWorkbookSourceRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class ExcelWorkbookSource(IConfiguration configuration) : IExcelWorkbookSource
{
    private static readonly HttpClient Client = CreateClient();
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? graphAccessToken;
    private DateTimeOffset graphAccessTokenExpiresAt;

    public Task<MemoryStream> OpenAsync(
        ExcelWorkbookSourceRequest request,
        CancellationToken cancellationToken = default) => request.SourceType switch
        {
            ExcelWorkbookSourceTypes.LocalFile => OpenFileAsync(
                Required(request.LocalPath, "Excel file path"),
                managedReference: false,
                cancellationToken),
            ExcelWorkbookSourceTypes.ExcelUrl => OpenUrlAsync(
                Required(request.SourceUrl, "Excel download link"),
                cancellationToken),
            ExcelWorkbookSourceTypes.UploadedExcel => OpenFileAsync(
                Required(request.ManagedReference, "Uploaded Excel file"),
                managedReference: true,
                cancellationToken),
            ExcelWorkbookSourceTypes.MicrosoftGraphExcel => OpenMicrosoftGraphAsync(
                request.SharingUrl,
                request.DriveId,
                request.ItemId,
                cancellationToken),
            _ => throw new InvalidOperationException("The configured Excel source is not supported.")
        };

    private async Task<MemoryStream> OpenFileAsync(
        string pathOrReference,
        bool managedReference,
        CancellationToken cancellationToken)
    {
        string path;
        if (managedReference)
        {
            var fileName = Path.GetFileName(pathOrReference);
            if (!string.Equals(fileName, pathOrReference, StringComparison.Ordinal)
                || !fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The stored Excel file reference is invalid.");
            path = Path.Combine(ExcelWorkbookSourceStorage.UploadRoot(configuration), fileName);
        }
        else
        {
            path = Path.GetFullPath(pathOrReference);
        }

        if (!File.Exists(path))
        {
            var message = managedReference
                ? "The uploaded Excel file is no longer available."
                : $"Excel file not found: {path}";
            throw new FileNotFoundException(message, path);
        }

        var info = new FileInfo(path);
        if (info.Length > ExcelWorkbookLimits.MaximumWorkbookBytes)
            throw new InvalidOperationException("The Excel workbook is larger than the 25 MB limit.");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        return await BufferWorkbookAsync(stream, info.Length, cancellationToken);
    }

    private async Task<MemoryStream> OpenUrlAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter a valid HTTP or HTTPS Excel download link.", nameof(sourceUrl));

        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await BufferWorkbookAsync(source, response.Content.Headers.ContentLength, cancellationToken);
    }

    private async Task<MemoryStream> OpenMicrosoftGraphAsync(
        string? sharingUrl,
        string? driveId,
        string? itemId,
        CancellationToken cancellationToken)
    {
        var token = await GetGraphAccessTokenAsync(cancellationToken);
        var uri = string.IsNullOrWhiteSpace(sharingUrl)
            ? BuildGraphItemContentUri(
                Required(driveId, "Microsoft Graph drive ID"),
                Required(itemId, "Microsoft Graph item ID"))
            : BuildGraphSharingContentUri(sharingUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateGraphDownloadException(response);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await BufferWorkbookAsync(source, response.Content.Headers.ContentLength, cancellationToken);
    }

    internal static string BuildGraphSharingContentUri(string sharingUrl)
    {
        var normalized = ExcelWorkbookSourceValidation.ValidateMicrosoftSharingUrl(sharingUrl);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized))
            .TrimEnd('=')
            .Replace('/', '_')
            .Replace('+', '-');
        return $"https://graph.microsoft.com/v1.0/shares/u!{encoded}/driveItem/content";
    }

    private static string BuildGraphItemContentUri(string driveId, string itemId) =>
        $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/content";

    private static InvalidOperationException CreateGraphDownloadException(HttpResponseMessage response)
    {
        var reason = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Microsoft Graph authentication failed. Verify the TeamHub application credentials.",
            System.Net.HttpStatusCode.Forbidden => "The TeamHub application does not have permission to read this workbook.",
            System.Net.HttpStatusCode.NotFound => "The SharePoint or OneDrive workbook link was not found or is not accessible to TeamHub.",
            _ => $"Microsoft Graph could not download the workbook ({(int)response.StatusCode} {response.ReasonPhrase})."
        };
        return new InvalidOperationException(reason);
    }

    private async Task<string> GetGraphAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(graphAccessToken)
            && graphAccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return graphAccessToken;

        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(graphAccessToken)
                && graphAccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return graphAccessToken;

            var tenantId = Required(configuration["TeamExcel:MicrosoftGraph:TenantId"], "Microsoft Graph tenant ID");
            var clientId = Required(configuration["TeamExcel:MicrosoftGraph:ClientId"], "Microsoft Graph client ID");
            var clientSecret = Required(configuration["TeamExcel:MicrosoftGraph:ClientSecret"], "Microsoft Graph client secret");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            });
            using var response = await Client.PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
                content,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Microsoft Graph authentication failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            graphAccessToken = payload.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Microsoft Graph did not return an access token.");
            var expiresIn = payload.RootElement.TryGetProperty("expires_in", out var expires)
                ? expires.GetInt32()
                : 3600;
            graphAccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return graphAccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static async Task<MemoryStream> BufferWorkbookAsync(
        Stream source,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength is > ExcelWorkbookLimits.MaximumWorkbookBytes)
            throw new InvalidOperationException("The Excel workbook is larger than the 25 MB limit.");

        var buffered = contentLength is > 0 and <= ExcelWorkbookLimits.MaximumWorkbookBytes
            ? new MemoryStream((int)contentLength.Value)
            : new MemoryStream();
        var buffer = new byte[81920];
        var total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > ExcelWorkbookLimits.MaximumWorkbookBytes)
                    throw new InvalidOperationException("The Excel workbook is larger than the 25 MB limit.");
                await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total == 0) throw new InvalidOperationException("The Excel workbook is empty.");
            buffered.Position = 0;
            return buffered;
        }
        catch
        {
            await buffered.DisposeAsync();
            throw;
        }
    }

    private static string Required(string? value, string label) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{label} is not configured.")
            : value.Trim();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = ExcelWorkbookLimits.MaximumWorkbookBytes
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TeamHub-ExcelImport/1.0");
        return client;
    }
}

public static class ExcelWorkbookSourceStorage
{
    public static string UploadRoot(IConfiguration configuration)
    {
        var configured = configuration["TeamExcel:UploadDirectory"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "data", "team-excel")
            : configured.Trim();
        return Path.GetFullPath(path);
    }
}

public static class ExcelWorkbookSourceServiceCollectionExtensions
{
    public static IServiceCollection AddExcelWorkbookSources(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(configuration);
        services.TryAddSingleton<IExcelWorkbookSource, ExcelWorkbookSource>();
        return services;
    }
}
