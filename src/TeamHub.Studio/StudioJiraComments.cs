using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamHub.Studio;

public sealed record StudioJiraCommentQuery(
    string StudioId, string TicketId, string RequestingUserId, bool AllowPrivilegedDefaultCredential);

public sealed record StudioJiraCommentPart(string Kind, string Text, string? Url = null, string? AttachmentId = null);
public sealed record StudioJiraComment(
    string Id, string Author, DateTimeOffset Created, DateTimeOffset? Updated,
    IReadOnlyList<StudioJiraCommentPart> Parts);
public sealed record StudioJiraCommentResult(
    string TicketId, string TicketUrl, bool IsUsingSharedCredential, IReadOnlyList<StudioJiraComment> Comments);
public sealed record StudioJiraCommentImage(byte[] Content, string ContentType);
public sealed record StudioJiraMilestoneReviewCommentRequest(
    string TicketId,
    string RequestingUserId,
    Guid FeedbackId,
    string? CommentId,
    string ProjectName,
    string MilestoneName,
    string Reviewer,
    string SessionDetails,
    string WhatGood,
    string NeedImprovements,
    string AdditionalFeedback);
public sealed record StudioJiraMilestoneReviewCommentResult(string CommentId);

public interface IStudioJiraCommentService
{
    Task<StudioJiraCommentResult> GetCommentsAsync(StudioJiraCommentQuery query, CancellationToken cancellationToken = default);
    Task<StudioJiraCommentImage> GetImageAsync(
        StudioJiraCommentQuery query, string commentId, string attachmentId, CancellationToken cancellationToken = default);
    Task<StudioJiraMilestoneReviewCommentResult> UpsertMilestoneReviewCommentAsync(
        StudioJiraMilestoneReviewCommentRequest request, CancellationToken cancellationToken = default);
}

public sealed class StudioJiraCommentException(string message) : Exception(message);

internal sealed record JiraCommentAttachment(string Id, string FileName, string ContentUrl, string ContentType);

internal sealed partial class JiraStudioCommentService(
    HttpClient httpClient,
    IStudioDirectoryService studioDirectory,
    IAtlassianConfigurationService configuration,
    IAtlassianCredentialAccessor credentials) : IStudioJiraCommentService
{
    private const int MaximumImageBytes = 10 * 1024 * 1024;

    public async Task<StudioJiraCommentResult> GetCommentsAsync(
        StudioJiraCommentQuery query, CancellationToken cancellationToken = default)
    {
        var context = await GetIssueContextAsync(query, cancellationToken);
        var comments = new Dictionary<string, StudioJiraComment>(StringComparer.Ordinal);
        var startAt = 0;
        for (var page = 0; page < 100; page++)
        {
            using var document = await GetJsonAsync(
                $"{context.IssueUrl}/comment?startAt={startAt}&maxResults=100", context.Credential, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("comments", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new StudioJiraCommentException("Jira returned an invalid comment list.");
            foreach (var item in items.EnumerateArray())
            {
                var comment = ParseComment(item, context.Attachments);
                comments[comment.Id] = comment;
            }
            if (!root.TryGetProperty("total", out var total) || !total.TryGetInt32(out var count) || count < 0)
                throw new StudioJiraCommentException("Jira did not return valid comment pagination information.");
            startAt += items.GetArrayLength();
            if (startAt >= count)
                return new StudioJiraCommentResult(query.TicketId,
                    $"{context.BaseUrl}/browse/{Uri.EscapeDataString(query.TicketId)}",
                    context.Credential.IsShared,
                    comments.Values.OrderByDescending(comment => comment.Created)
                        .ThenByDescending(comment => comment.Id, StringComparer.Ordinal).ToList());
            if (items.GetArrayLength() == 0)
                throw new StudioJiraCommentException("Jira stopped returning comments before the last page. Reopen the preview to try again.");
        }
        throw new StudioJiraCommentException("This ticket exceeds the preview limit of 100 comment pages. Open the ticket in Jira to view all comments.");
    }

    public async Task<StudioJiraCommentImage> GetImageAsync(
        StudioJiraCommentQuery query, string commentId, string attachmentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commentId) || string.IsNullOrWhiteSpace(attachmentId)
            || !NumericId().IsMatch(commentId) || !NumericId().IsMatch(attachmentId))
            throw new StudioJiraCommentException("Select a valid Jira comment image.");
        var context = await GetIssueContextAsync(query, cancellationToken);
        using var document = await GetJsonAsync(
            $"{context.IssueUrl}/comment/{commentId}", context.Credential, cancellationToken);
        var comment = ParseComment(document.RootElement, context.Attachments);
        if (comment.Id != commentId
            || !comment.Parts.Any(part => part.Kind == "image" && part.AttachmentId == attachmentId))
            throw new StudioJiraCommentException("This image is not referenced by the requested visible comment.");
        var attachment = context.Attachments.Single(item => item.Id == attachmentId);
        if (!Uri.TryCreate(attachment.ContentUrl, UriKind.Absolute, out var uri)
            || !Uri.TryCreate(context.BaseUrl + "/", UriKind.Absolute, out var baseUri)
            || !baseUri.IsBaseOf(uri) || uri.UserInfo.Length != 0
            || uri.Scheme is not ("https" or "http"))
            throw new StudioJiraCommentException("This image is not hosted under the configured Jira URL. Open the comment in Jira to view it.");

        using var request = JiraStudioTicketService.CreateJiraRequest(uri.AbsoluteUri, context.Credential);
        request.Headers.Accept.Clear();
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
            throw new StudioJiraCommentException("This image format cannot be previewed safely. Open the comment in Jira.");
        if (response.Content.Headers.ContentLength > MaximumImageBytes)
            throw new StudioJiraCommentException("This image exceeds the 10 MB preview limit. Open the comment in Jira.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaximumImageBytes)
                throw new StudioJiraCommentException("This image exceeds the 10 MB preview limit. Open the comment in Jira.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        if (!HasImageSignature(bytes, contentType))
            throw new StudioJiraCommentException("Jira returned invalid image content. Open the comment in Jira.");
        return new StudioJiraCommentImage(bytes, contentType);
    }

    public async Task<StudioJiraMilestoneReviewCommentResult> UpsertMilestoneReviewCommentAsync(
        StudioJiraMilestoneReviewCommentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RequestingUserId)
            || !TicketKey().IsMatch(request.TicketId)
            || request.FeedbackId == Guid.Empty)
            throw new StudioJiraCommentException("A valid Jira ticket, reviewer, and feedback row are required.");

        var prefix = request.TicketId[..request.TicketId.LastIndexOf('-')];
        var candidates = new List<string>();
        foreach (var studio in (await studioDirectory.GetStudiosAsync(cancellationToken)).Where(item => item.IsActive))
        {
            var mapping = await configuration.GetStudioMappingAsync(studio.Id, cancellationToken);
            if (mapping is not null
                && mapping.JiraProjectKeys.Contains(prefix, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(mapping.JiraStudioComponent))
                candidates.Add(studio.Id);
        }
        if (candidates.Count != 1)
            throw new StudioJiraCommentException(candidates.Count == 0
                ? "The Jira ticket project is not mapped to an active studio."
                : "The Jira ticket project maps to multiple studios. Configure a unique project mapping before syncing feedback.");

        IssueContext context;
        try
        {
            context = await GetIssueContextAsync(
                new StudioJiraCommentQuery(candidates[0], request.TicketId, request.RequestingUserId, false),
                cancellationToken,
                requirePersonalWriteCredential: true);
        }
        catch (StudioJiraCommentException)
        {
            throw;
        }
        if (context.Credential.IsShared || context.Credential.IsReadOnly)
            throw new StudioJiraCommentException("Milestone feedback requires your personal Jira connection with permission to create and edit comments. Shared read-only access cannot be used.");

        var marker = $"[TeamHub Milestone Review Feedback: {request.FeedbackId:D}]";
        if (!string.IsNullOrWhiteSpace(request.CommentId) && !NumericId().IsMatch(request.CommentId))
            throw new StudioJiraCommentException("The saved Jira feedback comment ID is invalid.");
        var commentId = request.CommentId
            ?? await FindManagedCommentIdAsync(context, marker, cancellationToken);
        var body = BuildMilestoneReviewBody(request, marker);
        var method = string.IsNullOrWhiteSpace(commentId) ? HttpMethod.Post : HttpMethod.Put;
        var url = string.IsNullOrWhiteSpace(commentId)
            ? $"{context.IssueUrl}/comment"
            : $"{context.IssueUrl}/comment/{Uri.EscapeDataString(commentId)}";
        using var httpRequest = JiraStudioTicketService.CreateJiraRequest(url, context.Credential);
        httpRequest.Method = method;
        httpRequest.Content = System.Net.Http.Json.JsonContent.Create(new { body });
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Jira rejected your personal credential. Update My Atlassian Connection.",
                HttpStatusCode.Forbidden => "Your Jira account does not have permission to create or edit comments on this ticket.",
                HttpStatusCode.NotFound => "The Jira ticket or feedback comment was not found or is no longer accessible.",
                HttpStatusCode.TooManyRequests => "Jira temporarily limited comment updates. Please try again shortly.",
                _ => $"Jira returned HTTP {(int)response.StatusCode} while saving the feedback comment."
            };
            throw new StudioJiraCommentException(reason);
        }

        if (!string.IsNullOrWhiteSpace(commentId))
            return new StudioJiraMilestoneReviewCommentResult(commentId);
        using var result = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var createdId = result.RootElement.TryGetProperty("id", out var id)
            ? id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString()
            : null;
        if (string.IsNullOrWhiteSpace(createdId) || !NumericId().IsMatch(createdId))
            throw new StudioJiraCommentException("Jira created a comment but did not return a valid comment ID. Save again to reconcile the comment.");
        return new StudioJiraMilestoneReviewCommentResult(createdId);
    }

    private async Task<string?> FindManagedCommentIdAsync(
        IssueContext context, string marker, CancellationToken cancellationToken)
    {
        var startAt = 0;
        for (var page = 0; page < 100; page++)
        {
            using var document = await GetJsonAsync(
                $"{context.IssueUrl}/comment?startAt={startAt}&maxResults=100", context.Credential, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("comments", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new StudioJiraCommentException("Jira returned an invalid comment list while locating the feedback comment.");
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("body", out var body) || !ContainsText(body, marker)) continue;
                var id = item.TryGetProperty("id", out var idElement)
                    ? idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.ToString()
                    : null;
                if (id is not null && NumericId().IsMatch(id)) return id;
            }
            if (!root.TryGetProperty("total", out var total) || !total.TryGetInt32(out var count))
                throw new StudioJiraCommentException("Jira did not return valid pagination information while locating the feedback comment.");
            startAt += items.GetArrayLength();
            if (startAt >= count) return null;
            if (items.GetArrayLength() == 0)
                throw new StudioJiraCommentException("Jira stopped returning comments before the feedback comment could be located.");
        }
        throw new StudioJiraCommentException("The ticket has too many comments to safely locate the existing feedback comment.");
    }

    private static bool ContainsText(JsonElement element, string value)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString()?.Contains(value, StringComparison.Ordinal) == true;
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property => ContainsText(property.Value, value));
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(child => ContainsText(child, value));
        return false;
    }

    private static string BuildMilestoneReviewBody(
        StudioJiraMilestoneReviewCommentRequest request, string marker) =>
        $"""
        Milestone Review Feedback
        Project: {request.ProjectName}
        Milestone: {request.MilestoneName}
        Reviewer: {request.Reviewer}
        Reviewer email: {request.RequestingUserId}

        Session Details
        {request.SessionDetails}

        What's Good
        {request.WhatGood}

        Needs Improvement
        {request.NeedImprovements}

        Additional Feedback
        {request.AdditionalFeedback}

        {marker}
        """;

    private async Task<IssueContext> GetIssueContextAsync(
        StudioJiraCommentQuery query,
        CancellationToken cancellationToken,
        bool requirePersonalWriteCredential = false)
    {
        if (string.IsNullOrWhiteSpace(query.RequestingUserId) || string.IsNullOrWhiteSpace(query.StudioId)
            || string.IsNullOrWhiteSpace(query.TicketId) || !TicketKey().IsMatch(query.TicketId))
            throw new StudioJiraCommentException("Select a valid ticket and sign in to view comments.");
        var studio = await studioDirectory.GetStudioAsync(query.StudioId, cancellationToken);
        if (studio is null || !studio.IsActive)
            throw new StudioJiraCommentException("The selected studio is not active.");
        var settings = await configuration.GetSettingsAsync(cancellationToken);
        if (!settings.JiraEnabled || !Uri.TryCreate(settings.JiraBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("https" or "http") || baseUri.UserInfo.Length != 0)
            throw new StudioJiraCommentException("Jira integration is disabled or its base URL is not configured.");
        var mapping = await configuration.GetStudioMappingAsync(query.StudioId, cancellationToken);
        if (mapping is null || mapping.JiraProjectKeys.Count == 0 || string.IsNullOrWhiteSpace(mapping.JiraStudioComponent))
            throw new StudioJiraCommentException("The selected studio does not have a complete Jira mapping.");
        AtlassianResolvedCredential? credential;
        try
        {
            credential = await credentials.ResolveJiraCredentialAsync(
                query.RequestingUserId, query.AllowPrivilegedDefaultCredential, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new StudioJiraCommentException(exception.Message);
        }
        if (credential is null)
            throw new StudioJiraCommentException(requirePersonalWriteCredential
                ? "Milestone feedback requires your personal Jira connection with permission to create and edit comments."
                : "No Jira credential is available. Connect your account in My Atlassian Connection or ask an administrator to grant shared read-only access.");
        var baseUrl = settings.JiraBaseUrl.TrimEnd('/');
        var issueUrl = $"{baseUrl}/rest/api/2/issue/{Uri.EscapeDataString(query.TicketId)}";
        using var document = await GetJsonAsync(
            $"{issueUrl}?fields=project,components,attachment", credential, cancellationToken);
        var root = document.RootElement;
        var fields = root.GetProperty("fields");
        var projectKey = fields.GetProperty("project").GetProperty("key").GetString();
        var componentMatches = fields.GetProperty("components").EnumerateArray()
            .Any(component => string.Equals(component.GetProperty("name").GetString(),
                mapping.JiraStudioComponent, StringComparison.OrdinalIgnoreCase));
        if (!mapping.JiraProjectKeys.Contains(projectKey, StringComparer.OrdinalIgnoreCase) || !componentMatches)
            throw new StudioJiraCommentException("This ticket does not belong to the selected studio's configured Jira scope.");
        var attachments = fields.GetProperty("attachment").EnumerateArray().Select(item => new JiraCommentAttachment(
            item.GetProperty("id").GetString() ?? "",
            item.GetProperty("filename").GetString() ?? "",
            item.GetProperty("content").GetString() ?? "",
            item.GetProperty("mimeType").GetString() ?? "")).ToList();
        return new IssueContext(baseUrl, issueUrl, credential, attachments);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, AtlassianResolvedCredential credential, CancellationToken cancellationToken)
    {
        using var request = JiraStudioTicketService.CreateJiraRequest(url, credential);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new StudioJiraCommentException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Jira rejected the credential. Update your Atlassian connection or ask an administrator to check shared access.",
            HttpStatusCode.Forbidden => "Your Jira account does not have permission to view this comment or image.",
            HttpStatusCode.NotFound => "This Jira ticket, comment or image was not found or is not visible to your account.",
            HttpStatusCode.TooManyRequests => "Jira temporarily limited requests. Please try again shortly.",
            _ => $"Jira returned HTTP {(int)response.StatusCode} while loading comments or images."
        });
    }

    private static StudioJiraComment ParseComment(JsonElement item, IReadOnlyList<JiraCommentAttachment> attachments)
    {
        var id = item.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id) || !NumericId().IsMatch(id))
            throw new StudioJiraCommentException("Jira returned an invalid comment identifier.");
        var created = ParseDate(item.GetProperty("created").GetString());
        var updated = item.TryGetProperty("updated", out var date) && date.ValueKind == JsonValueKind.String
            ? ParseDate(date.GetString()) : (DateTimeOffset?)null;
        var author = item.TryGetProperty("author", out var authorObject)
            && authorObject.TryGetProperty("displayName", out var name) ? name.GetString() : null;
        return new StudioJiraComment(id,
            author ?? "Unknown author", created, updated,
            JiraCommentBodyParser.Parse(item.GetProperty("body"), attachments));
    }

    private static DateTimeOffset ParseDate(string? value)
    {
        // Jira Server uses offsets such as +0530 rather than the ISO +05:30 form.
        var normalized = value is null ? "" : OffsetSuffix().Replace(value, "$1:$2");
        if (!DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new StudioJiraCommentException("Jira returned an invalid comment timestamp.");
        return date;
    }

    private static bool HasImageSignature(byte[] bytes, string contentType) => contentType switch
    {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
        "image/gif" => bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*-[1-9][0-9]*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TicketKey();
    [GeneratedRegex(@"^[0-9]{1,30}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NumericId();
    [GeneratedRegex(@"([+-]\d{2})(\d{2})$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex OffsetSuffix();
    private sealed record IssueContext(string BaseUrl, string IssueUrl, AtlassianResolvedCredential Credential,
        IReadOnlyList<JiraCommentAttachment> Attachments);
}
