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
    string? SharingUrl = null,
    string? TenantId = null,
    string? ClientId = null,
    string? ClientSecret = null);

public interface IExcelWorkbookSource
{
    Task<MemoryStream> OpenAsync(
        ExcelWorkbookSourceRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class ExcelWorkbookSource(IConfiguration configuration) : IExcelWorkbookSource
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly HttpClient DirectClient = CreateDirectClient();
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
            ExcelWorkbookSourceTypes.MicrosoftGraphExcel => OpenSharePointOrOneDriveAsync(
                request,
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
                || !IsSupportedWorkbookName(fileName))
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

        if (MicrosoftCloudLinkHelper.IsMicrosoftCloudUrl(sourceUrl))
        {
            var candidates = MicrosoftCloudLinkHelper.GetDirectDownloadCandidateUrls(sourceUrl);
            var (directStream, requiresAuth, _) = await TryDirectDownloadAsync(candidates, cancellationToken);
            if (directStream is not null) return directStream;
            if (requiresAuth)
                throw new InvalidOperationException("The SharePoint or OneDrive link requires organization authentication. Use the 'SharePoint / OneDrive' or 'Upload from this computer' source option.");
        }

        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = await BufferWorkbookAsync(source, response.Content.Headers.ContentLength, cancellationToken);
        if (!MicrosoftCloudLinkHelper.IsSupportedExcelStream(buffer))
        {
            await buffer.DisposeAsync();
            throw new InvalidOperationException("The download link returned content that is not a valid Excel workbook (.xlsx or .xls).");
        }
        return buffer;
    }

    private async Task<MemoryStream> OpenSharePointOrOneDriveAsync(
        ExcelWorkbookSourceRequest request,
        CancellationToken cancellationToken)
    {
        var sharingUrl = request.SharingUrl ?? request.SourceUrl;

        if (!string.IsNullOrWhiteSpace(sharingUrl))
        {
            var normalizedUrl = ExcelWorkbookSourceValidation.ValidateMicrosoftSharingUrl(sharingUrl);
            var candidates = MicrosoftCloudLinkHelper.GetDirectDownloadCandidateUrls(normalizedUrl);
            var (stream, requiresAuth, failureReason) = await TryDirectDownloadAsync(candidates, cancellationToken);
            if (stream is not null)
            {
                return stream;
            }

            var credentials = await ResolveGraphCredentialsAsync(request, cancellationToken);
            if (credentials.HasCredentials)
            {
                return await OpenMicrosoftGraphAsync(
                    credentials.TenantId!,
                    credentials.ClientId!,
                    credentials.ClientSecret!,
                    normalizedUrl,
                    request.DriveId,
                    request.ItemId,
                    cancellationToken);
            }

            var message = requiresAuth
                ? "The SharePoint or OneDrive workbook link requires organization authentication (sign-in required).\n\n" +
                  "To resolve this, you can:\n" +
                  "1. In SharePoint or OneDrive, select 'Share' > 'Copy link' and choose 'Anyone with the link can view' (if allowed by your organization),\n" +
                  "2. Or enter Microsoft Graph application credentials (Tenant ID, Client ID, Client Secret) in Configuration,\n" +
                  "3. Or download the workbook and upload it directly using the 'Upload from this computer' option."
                : $"Could not access the SharePoint or OneDrive workbook link: {failureReason ?? "Workbook could not be downloaded."}\n\n" +
                  "Verify the link, or upload the workbook directly using 'Upload from this computer'.";

            throw new InvalidOperationException(message);
        }

        if (!string.IsNullOrWhiteSpace(request.DriveId) && !string.IsNullOrWhiteSpace(request.ItemId))
        {
            var credentials = await ResolveGraphCredentialsAsync(request, cancellationToken);
            if (!credentials.HasCredentials)
            {
                throw new InvalidOperationException(
                    "Microsoft Graph credentials (Tenant ID, Client ID, Client Secret) are required to access workbooks using Drive ID and Item ID. Configure them in Configuration.");
            }

            return await OpenMicrosoftGraphAsync(
                credentials.TenantId!,
                credentials.ClientId!,
                credentials.ClientSecret!,
                sharingUrl: null,
                request.DriveId,
                request.ItemId,
                cancellationToken);
        }

        throw new InvalidOperationException("Enter a SharePoint or OneDrive workbook link.");
    }

    private async Task<(MemoryStream? Stream, bool RequiresAuthentication, string? FailureReason)> TryDirectDownloadAsync(
        IReadOnlyList<string> candidateUrls,
        CancellationToken cancellationToken)
    {
        var requiresAuth = false;
        string? failureReason = null;

        foreach (var candidateUrl in candidateUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var currentUrl = candidateUrl;
                var redirectsRemaining = 5;
                HttpResponseMessage? response = null;

                while (redirectsRemaining >= 0)
                {
                    if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        break;

                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Accept.ParseAdd("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet, application/vnd.ms-excel, application/octet-stream, */*");

                    response?.Dispose();
                    response = await DirectClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently
                        or System.Net.HttpStatusCode.Found
                        or System.Net.HttpStatusCode.SeeOther
                        or System.Net.HttpStatusCode.TemporaryRedirect
                        or (System.Net.HttpStatusCode)308)
                    {
                        var location = response.Headers.Location;
                        if (location is null) break;

                        var nextUri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        if (IsLoginUrl(nextUri))
                        {
                            requiresAuth = true;
                            failureReason = "The link redirected to Microsoft organization sign-in.";
                            break;
                        }

                        currentUrl = nextUri.ToString();
                        redirectsRemaining--;
                        continue;
                    }

                    break;
                }

                if (response is null) continue;
                using (response)
                {
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                        or System.Net.HttpStatusCode.Forbidden)
                    {
                        requiresAuth = true;
                        failureReason = $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        failureReason ??= $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
                        continue;
                    }

                    await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var buffer = await BufferWorkbookAsync(contentStream, response.Content.Headers.ContentLength, cancellationToken);
                    if (MicrosoftCloudLinkHelper.IsSupportedExcelStream(buffer))
                    {
                        return (buffer, false, null);
                    }

                    if (IsHtmlLoginPage(buffer))
                    {
                        requiresAuth = true;
                        failureReason = "The link returned an organization sign-in page instead of an Excel file.";
                    }
                    await buffer.DisposeAsync();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                failureReason ??= ex.Message;
            }
        }

        return (null, requiresAuth, failureReason);
    }

    private static bool IsLoginUrl(Uri uri)
    {
        var host = uri.Host;
        return host.EndsWith("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("login.live.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("login.windows.net", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/adfs/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHtmlLoginPage(MemoryStream stream)
    {
        var position = stream.Position;
        stream.Position = 0;
        try
        {
            var length = Math.Min(4096, (int)stream.Length);
            Span<byte> bytes = stackalloc byte[length];
            var read = stream.Read(bytes);
            if (read == 0) return false;
            var text = Encoding.UTF8.GetString(bytes[..read]);
            return text.Contains("<html", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Federation", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            stream.Position = position;
        }
    }

    private async Task<(bool HasCredentials, string? TenantId, string? ClientId, string? ClientSecret)> ResolveGraphCredentialsAsync(
        ExcelWorkbookSourceRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = request.TenantId;
        var clientId = request.ClientId;
        var clientSecret = request.ClientSecret;

        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret))
        {
            var configTenant = configuration["TeamExcel:MicrosoftGraph:TenantId"];
            var configClient = configuration["TeamExcel:MicrosoftGraph:ClientId"];
            var configSecret = configuration["TeamExcel:MicrosoftGraph:ClientSecret"];
            if (!string.IsNullOrWhiteSpace(configTenant)
                && !string.IsNullOrWhiteSpace(configClient)
                && !string.IsNullOrWhiteSpace(configSecret))
            {
                tenantId = string.IsNullOrWhiteSpace(tenantId) ? configTenant : tenantId;
                clientId = string.IsNullOrWhiteSpace(clientId) ? configClient : clientId;
                clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? configSecret : clientSecret;
            }
        }

        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret))
        {
            var connectionString = configuration.GetConnectionString("TeamDb");
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT TenantId, ClientId, ClientSecret FROM MilestoneSourceSettings WHERE Id = 1;";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        var dbTenant = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var dbClient = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var dbSecret = reader.IsDBNull(2) ? null : reader.GetString(2);
                        tenantId = string.IsNullOrWhiteSpace(tenantId) ? dbTenant : tenantId;
                        clientId = string.IsNullOrWhiteSpace(clientId) ? dbClient : clientId;
                        clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? dbSecret : clientSecret;
                    }
                }
                catch
                {
                    // Ignore DB read failure if table or columns don't exist yet
                }
            }
        }

        var hasCreds = !string.IsNullOrWhiteSpace(tenantId)
            && !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(clientSecret);

        return (hasCreds, tenantId?.Trim(), clientId?.Trim(), clientSecret?.Trim());
    }

    private async Task<MemoryStream> OpenMicrosoftGraphAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        string? sharingUrl,
        string? driveId,
        string? itemId,
        CancellationToken cancellationToken)
    {
        var token = await GetGraphAccessTokenAsync(tenantId, clientId, clientSecret, cancellationToken);
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
            System.Net.HttpStatusCode.Forbidden => "The TeamHub application does not have permission to read this workbook. Verify that the app registration has Files.Read.All or Sites.Read.All permissions.",
            System.Net.HttpStatusCode.NotFound => "The SharePoint or OneDrive workbook link was not found or is not accessible to Microsoft Graph. Note: For Microsoft Graph, use a sharing link from 'Share' > 'Copy link'.",
            _ => $"Microsoft Graph could not download the workbook ({(int)response.StatusCode} {response.ReasonPhrase})."
        };
        return new InvalidOperationException(reason);
    }

    private async Task<string> GetGraphAccessTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
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
                throw new InvalidOperationException($"Microsoft Graph authentication failed ({(int)response.StatusCode} {response.ReasonPhrase}). Check Tenant ID, Client ID, and Client Secret.");
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

    private static bool IsSupportedWorkbookName(string fileName) =>
        fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase);

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

    private static HttpClient CreateDirectClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = ExcelWorkbookLimits.MaximumWorkbookBytes
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
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
