using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamHub.Authentication;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;
using TeamHub.Web.Pages;

namespace TeamHub.Tests;

public sealed class StudioJiraCommentTests
{
    private const string IssueJson = """
        {"fields":{"project":{"key":"SUP"},"components":[{"name":"StudioA"}],
          "attachment":[{"id":"11","filename":"image.png","mimeType":"image/png",
            "content":"https://jira.example.test/secure/attachment/11/image.png"}]}}
        """;
    private const string CommentJson = """
        {"id":"10","author":{"displayName":"Asha"},"created":"2026-10-09T10:00:00.000+0530",
         "updated":"2026-10-09T11:00:00.000+0530","body":"Reviewed build.\n!image.png|thumbnail!"}
        """;

    [Fact]
    public async Task GetComments_FetchesEveryPageAndSortsNewestFirst()
    {
        var requests = new List<string>();
        await using var host = await Host.CreateAsync(request =>
        {
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("personal-test-token");
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (!request.RequestUri.AbsolutePath.EndsWith("/comment")) return Json(IssueJson);
            var isSecond = request.RequestUri.Query.Contains("startAt=1");
            return Json(isSecond
                ? $$"""{"total":2,"comments":[{{CommentJson}}]}"""
                : """{"total":2,"comments":[{"id":"9","author":{"displayName":"Dev"},"created":"2026-10-08T09:00:00.000+0000","body":"Waiting."}]}""");
        });

        var result = await host.Service.GetCommentsAsync(host.Query);

        requests.Should().HaveCount(3);
        result.Comments.Select(comment => comment.Id).Should().Equal("10", "9");
        result.Comments[0].Created.Offset.Should().Be(TimeSpan.FromHours(5.5));
        result.Comments[0].Author.Should().Be("Asha");
        result.Comments[0].Parts.Should().Contain(part => part.Kind == "image" && part.AttachmentId == "11");
        result.TicketUrl.Should().Be("https://jira.example.test/browse/SUP-1");
    }

    [Fact]
    public async Task GetComments_RejectsTicketsOutsideStudioMappingBeforeReadingComments()
    {
        var calls = 0;
        await using var host = await Host.CreateAsync(_ =>
        {
            calls++;
            return Json(IssueJson.Replace("StudioA", "AnotherStudio"));
        });
        var act = () => host.Service.GetCommentsAsync(host.Query);
        await act.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*configured Jira scope*");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetComments_UsesSharedReadOnlyCredentialOnlyWithExplicitGrant()
    {
        await using var host = await Host.CreateAsync(request =>
        {
            request.Headers.Authorization!.Parameter.Should().Be("shared-test-token");
            return Json(request.RequestUri!.AbsolutePath.EndsWith("/comment")
                ? """{"total":0,"comments":[]}""" : IssueJson);
        });
        await host.ConfigureSharedAccessAsync();
        var denied = () => host.Service.GetCommentsAsync(host.Query);
        await denied.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*credential*");
        var result = await host.Service.GetCommentsAsync(host.Query with { AllowPrivilegedDefaultCredential = true });
        result.IsUsingSharedCredential.Should().BeTrue();
        result.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task GetComments_DeniesInactiveStudioBeforeNetworkRequest()
    {
        var calls = 0;
        await using var host = await Host.CreateAsync(_ => { calls++; return Json(IssueJson); });
        await host.DeactivateStudioAsync();
        var act = () => host.Service.GetCommentsAsync(host.Query);
        await act.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*not active*");
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("SUP-1/../../other")]
    [InlineData("")]
    public async Task GetComments_RejectsInvalidTicketKeysBeforeNetworkRequest(string ticketId)
    {
        var calls = 0;
        await using var host = await Host.CreateAsync(_ => { calls++; return Json(IssueJson); });
        var act = () => host.Service.GetCommentsAsync(host.Query with { TicketId = ticketId });
        await act.Should().ThrowAsync<StudioJiraCommentException>();
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetComments_SurfacesJiraPermissionAndServiceErrors(HttpStatusCode status)
    {
        await using var host = await Host.CreateAsync(_ => new HttpResponseMessage(status));
        var act = () => host.Service.GetCommentsAsync(host.Query);
        await act.Should().ThrowAsync<StudioJiraCommentException>();
    }

    [Fact]
    public async Task GetComments_RejectsIncompletePaginationRatherThanShowingPartialResults()
    {
        await using var host = await Host.CreateAsync(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/comment")
                ? Json("""{"total":1,"comments":[]}""") : Json(IssueJson));
        var act = () => host.Service.GetCommentsAsync(host.Query);
        await act.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*before the last page*");
    }

    [Fact]
    public async Task GetImage_ChecksCommentVisibilityAndAttachmentMembershipBeforeProxying()
    {
        var calls = new List<string>();
        await using var host = await Host.CreateAsync(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.Contains("/secure/attachment/")) return Png();
            return Json(request.RequestUri.AbsolutePath.EndsWith("/comment/10") ? CommentJson : IssueJson);
        });
        var image = await host.Service.GetImageAsync(host.Query, "10", "11");
        image.ContentType.Should().Be("image/png");
        image.Content.Should().NotBeEmpty();
        calls.Should().Equal("/rest/api/2/issue/SUP-1", "/rest/api/2/issue/SUP-1/comment/10",
            "/secure/attachment/11/image.png");
    }

    [Fact]
    public async Task GetImage_DoesNotExposeUnreferencedAttachments()
    {
        var downloaded = false;
        await using var host = await Host.CreateAsync(request =>
        {
            downloaded |= request.RequestUri!.AbsolutePath.Contains("/secure/attachment/");
            return Json(request.RequestUri.AbsolutePath.EndsWith("/comment/10")
                ? CommentJson.Replace("!image.png|thumbnail!", "No images.") : IssueJson);
        });
        var act = () => host.Service.GetImageAsync(host.Query, "10", "11");
        await act.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*not referenced*");
        downloaded.Should().BeFalse();
    }

    [Fact]
    public async Task GetImage_DeniesRestrictedCommentEvenWhenTicketAttachmentIsVisible()
    {
        var downloaded = false;
        await using var host = await Host.CreateAsync(request =>
        {
            downloaded |= request.RequestUri!.AbsolutePath.Contains("/secure/attachment/");
            return request.RequestUri.AbsolutePath.EndsWith("/comment/10")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json(IssueJson);
        });
        var act = () => host.Service.GetImageAsync(host.Query, "10", "11");
        await act.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*permission*");
        downloaded.Should().BeFalse();
    }

    [Fact]
    public async Task GetImage_RejectsOffHostAttachmentWithoutSendingCredential()
    {
        var downloaded = false;
        await using var host = await Host.CreateAsync(request =>
        {
            downloaded |= request.RequestUri!.Host != "jira.example.test";
            return Json(request.RequestUri.AbsolutePath.EndsWith("/comment/10") ? CommentJson
                : IssueJson.Replace("https://jira.example.test/secure/attachment", "https://external.example.test/secure/attachment"));
        });
        var act = () => host.Service.GetImageAsync(host.Query, "10", "11");
        await act.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*not hosted*");
        downloaded.Should().BeFalse();
    }

    [Theory]
    [InlineData("text/html", false, false)]
    [InlineData("image/svg+xml", false, false)]
    [InlineData("image/png", true, false)]
    [InlineData("image/png", false, true)]
    public async Task GetImage_RejectsUnsafeInvalidOrOversizedContent(string contentType, bool invalid, bool oversized)
    {
        await using var host = await Host.CreateAsync(request =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/secure/attachment/"))
                return Json(request.RequestUri.AbsolutePath.EndsWith("/comment/10") ? CommentJson : IssueJson);
            var response = Png();
            response.Content.Headers.ContentType = new(contentType);
            if (invalid) response.Content = new StringContent("<html>not an image</html>");
            response.Content.Headers.ContentType = new(contentType);
            if (oversized) response.Content.Headers.ContentLength = 10 * 1024 * 1024 + 1;
            return response;
        });
        var act = () => host.Service.GetImageAsync(host.Query, "10", "11");
        await act.Should().ThrowAsync<StudioJiraCommentException>();
    }

    [Fact]
    public async Task UpsertMilestoneReviewComment_CreatesUpdatesAndRecoversByMarkerUsingPersonalCredential()
    {
        var feedbackId = Guid.NewGuid();
        var calls = new List<(HttpMethod Method, string Path)>();
        await using var host = await Host.CreateAsync(request =>
        {
            request.Headers.Authorization!.Parameter.Should().Be("personal-test-token");
            var path = request.RequestUri!.AbsolutePath;
            calls.Add((request.Method, path));
            if (path.EndsWith("/issue/SUP-1", StringComparison.Ordinal)) return Json(IssueJson);
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                body.RootElement.GetProperty("body").GetString().Should().Contain("Reviewer email: reader@example.com");
                body.RootElement.GetProperty("body").GetString().Should().Contain($"[TeamHub Milestone Review Feedback: {feedbackId:D}]");
                var text = body.RootElement.GetProperty("body").GetString();
                text.Should().Contain($"Session Details{Environment.NewLine}PC, 1 hour");
                text.Should().Contain($"What's Good{Environment.NewLine}Good");
                text.Should().Contain($"Needs Improvement{Environment.NewLine}Improve");
                text.Should().Contain($"Additional Feedback{Environment.NewLine}Art notes");
                text.Should().NotContain("What's Bad");
                return request.Method == HttpMethod.Post
                    ? Json("""{"id":"42"}""")
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/comment", StringComparison.Ordinal)
                && calls.Count(call => call.Method == HttpMethod.Get
                    && call.Path.EndsWith("/comment", StringComparison.Ordinal)) == 1)
                return Json("""{"total":0,"comments":[]}""");
            if (request.RequestUri.Query.Contains("startAt=0", StringComparison.Ordinal))
                return Json($$"""{"total":1,"comments":[{"id":"42","body":"[TeamHub Milestone Review Feedback: {{feedbackId:D}}]"}]}""");
            throw new InvalidOperationException($"Unexpected Jira request: {request.Method} {path}");
        });
        var request = new StudioJiraMilestoneReviewCommentRequest(
            "SUP-1", host.Query.RequestingUserId, feedbackId, null,
            "Project", "Milestone", "Reviewer", "PC, 1 hour", "Good", "Improve", "Art notes");

        var created = await host.Service.UpsertMilestoneReviewCommentAsync(request);
        created.CommentId.Should().Be("42");
        var updated = await host.Service.UpsertMilestoneReviewCommentAsync(request with { CommentId = created.CommentId });
        updated.CommentId.Should().Be("42");
        var recovered = await host.Service.UpsertMilestoneReviewCommentAsync(request);
        recovered.CommentId.Should().Be("42");

        calls.Select(call => (call.Method.Method, call.Path)).Should().Equal(
            ("GET", "/rest/api/2/issue/SUP-1"),
            ("GET", "/rest/api/2/issue/SUP-1/comment"),
            ("POST", "/rest/api/2/issue/SUP-1/comment"),
            ("GET", "/rest/api/2/issue/SUP-1"),
            ("PUT", "/rest/api/2/issue/SUP-1/comment/42"),
            ("GET", "/rest/api/2/issue/SUP-1"),
            ("GET", "/rest/api/2/issue/SUP-1/comment"),
            ("PUT", "/rest/api/2/issue/SUP-1/comment/42"));
    }

    [Fact]
    public async Task UpsertMilestoneReviewComment_RejectsSharedReadOnlyCredential()
    {
        var calls = 0;
        await using var host = await Host.CreateAsync(_ => { calls++; return Json(IssueJson); });
        await host.ConfigureSharedAccessAsync();
        var request = new StudioJiraMilestoneReviewCommentRequest(
            "SUP-1", host.Query.RequestingUserId, Guid.NewGuid(), null,
            "Project", "Milestone", "Reviewer", "PC, 1 hour", "Good", "Improve", "Art notes");

        var act = () => host.Service.UpsertMilestoneReviewCommentAsync(request);

        await act.Should().ThrowAsync<StudioJiraCommentException>()
            .WithMessage("*personal Jira connection*");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task MilestoneFeedbackSyncsSharedProjectTicketUsingItsActualStudioComponent()
    {
        var writes = new List<string>();
        await using var host = await Host.CreateAsync(request =>
        {
            request.Headers.Authorization!.Parameter.Should().Be("personal-test-token");
            if (request.Method == HttpMethod.Put || request.Method == HttpMethod.Post)
            {
                writes.Add(request.RequestUri!.AbsolutePath);
                return Json("""{"id":"42"}""");
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/comment"))
                return Json("""{"total":0,"comments":[]}""");
            return Json(IssueJson.Replace("SUP", "SIEHP").Replace("StudioA", "StudioB"));
        });
        await host.ConfigureSharedProjectAsync("SIEHP");
        var feedback = new StudioJiraMilestoneReviewCommentRequest(
            "SIEHP-1206", host.Query.RequestingUserId, Guid.NewGuid(), null,
            "Project", "Milestone", "Reviewer", "PC", "Good", "Improve", "Art");
        var created = await host.Service.UpsertMilestoneReviewCommentAsync(feedback);
        var updated = await host.Service.UpsertMilestoneReviewCommentAsync(feedback with { CommentId = created.CommentId });
        updated.CommentId.Should().Be("42");
        writes.Should().Equal("/rest/api/2/issue/SIEHP-1206/comment", "/rest/api/2/issue/SIEHP-1206/comment/42");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TicketOnlyPreviewsAndImagesWorkWhenProjectIsSharedAcrossStudios(bool image)
    {
        await using var host = await Host.CreateAsync(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/secure/attachment/")) return Png();
            if (request.RequestUri.AbsolutePath.EndsWith("/comment/10")) return Json(CommentJson);
            if (request.RequestUri.AbsolutePath.EndsWith("/comment")) return Json($$"""{"total":1,"comments":[{{CommentJson}}]}""");
            return Json(IssueJson.Replace("SUP", "SIEHP").Replace("StudioA", "StudioB"));
        });
        await host.ConfigureSharedProjectAsync("SIEHP");
        var query = host.Query with { StudioId = null, TicketId = "SIEHP-1206" };
        if (image)
            (await host.Service.GetImageAsync(query, "10", "11")).ContentType.Should().Be("image/png");
        else
            (await host.Service.GetCommentsAsync(query)).Comments.Should().ContainSingle().Which.Id.Should().Be("10");
        var wrongStudio = () => host.Service.GetCommentsAsync(query with { StudioId = host.Query.StudioId });
        await wrongStudio.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*selected studio*scope*");
    }

    [Theory]
    [InlineData("AnotherStudio", "SUP")]
    [InlineData("StudioB", "UNMAPPED")]
    public async Task TicketOnlyRequestsRejectUnmatchedActualProjectOrComponentBeforeReadingOrWritingComments(
        string component, string project)
    {
        var calls = new List<string>();
        await using var host = await Host.CreateAsync(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return Json(IssueJson.Replace("SUP", project).Replace("StudioA", component));
        });
        await host.ConfigureSharedProjectAsync();
        var query = host.Query with { StudioId = null };
        var read = () => host.Service.GetCommentsAsync(query);
        await read.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*configured Jira scope*");
        var write = () => host.Service.UpsertMilestoneReviewCommentAsync(new(
            query.TicketId, query.RequestingUserId, Guid.NewGuid(), "42",
            "Project", "Milestone", "Reviewer", "", "", "", ""));
        await write.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*configured Jira scope*");
        calls.Should().Equal("/rest/api/2/issue/SUP-1", "/rest/api/2/issue/SUP-1");
    }

    [Fact]
    public async Task TicketOnlyRequestsRejectInactiveMappingsAndMissingProjectBeforeNetworkRequest()
    {
        var calls = 0;
        await using var host = await Host.CreateAsync(_ => { calls++; return Json(IssueJson); });
        var unmapped = () => host.Service.GetCommentsAsync(host.Query with { StudioId = null, TicketId = "UNMAPPED-1" });
        await unmapped.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*not mapped*");
        await host.DeactivateStudioAsync();
        var inactive = () => host.Service.GetCommentsAsync(host.Query with { StudioId = null });
        await inactive.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*not mapped*");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task TicketOnlyPreviewsRetainExplicitSharedReadOnlyCredentialGrant()
    {
        await using var host = await Host.CreateAsync(request =>
        {
            request.Headers.Authorization!.Parameter.Should().Be("shared-test-token");
            return Json(request.RequestUri!.AbsolutePath.EndsWith("/comment")
                ? """{"total":0,"comments":[]}""" : IssueJson.Replace("StudioA", "StudioB"));
        });
        await host.ConfigureSharedProjectAsync();
        await host.ConfigureSharedAccessAsync();
        var query = host.Query with { StudioId = null };
        var denied = () => host.Service.GetCommentsAsync(query);
        await denied.Should().ThrowAsync<StudioJiraCommentException>().WithMessage("*credential*");
        (await host.Service.GetCommentsAsync(query with { AllowPrivilegedDefaultCredential = true }))
            .IsUsingSharedCredential.Should().BeTrue();
    }

    [Fact]
    public void WikiBody_RendersExternalImagesAsLinksAndNeverRendersRawHtml()
    {
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(
            "<script>alert(1)</script>\n!https://external.example.test/image.png!\n!missing.png!\n[unsafe|https://user:password@example.test/x]"));
        var parts = JiraCommentBodyParser.Parse(body.RootElement, []);
        parts.Should().Contain(part => part.Kind == "text" && part.Text.Contains("<script>"));
        parts.Should().ContainSingle(part => part.Kind == "link")
            .Which.Url.Should().Be("https://external.example.test/image.png");
        parts.Should().Contain(part => part.Text.Contains("Unavailable in preview"));
        parts.Should().NotContain(part => part.Kind == "image");
    }

    [Fact]
    public void DocumentBody_PreservesTextLinksAndResolvableMediaAndDisclosesUnresolvedMedia()
    {
        using var body = JsonDocument.Parse("""
            {"type":"doc","content":[
              {"type":"paragraph","content":[{"type":"text","text":"Review pending"},
                {"type":"hardBreak"},{"type":"text","text":"Details","marks":[{"type":"link","attrs":{"href":"https://jira.example.test/browse/SUP-1"}}]}]},
              {"type":"mediaSingle","content":[{"type":"media","attrs":{"id":"11"}}]},
              {"type":"mediaSingle","content":[{"type":"media","attrs":{"id":"unresolved-cloud-media-id"}}]}]}
            """);
        var parts = JiraCommentBodyParser.Parse(body.RootElement,
            [new JiraCommentAttachment("11", "image.png", "https://jira.example.test/image.png", "image/png")]);
        parts.Should().Contain(part => part.Text == "Review pending");
        parts.Should().Contain(part => part.Kind == "link" && part.Text == "Details");
        parts.Should().ContainSingle(part => part.Kind == "image").Which.AttachmentId.Should().Be("11");
        parts.Should().Contain(part => part.Text.Contains("unresolved-cloud-media-id") && part.Text.Contains("Unavailable"));
    }

    [Fact]
    public async Task CommentPage_DeniesAccessBeforeCallingJiraAndDisablesCaching()
    {
        var service = new StubComments();
        var model = Page(service, false);
        (await model.OnGetAsync("studio", "SUP-1", CancellationToken.None)).Should().BeOfType<ForbidResult>();
        service.Calls.Should().Be(0);
        model.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task CommentPage_ReturnsImagesAndExplicitErrors()
    {
        var service = new StubComments();
        var model = Page(service, true);
        (await model.OnGetImageAsync("studio", "SUP-1", "10", "11", CancellationToken.None))
            .Should().BeOfType<FileContentResult>();
        service.Error = new StudioJiraCommentException("No Jira credential is available.");
        var failure = (JsonResult)await model.OnGetAsync("studio", "SUP-1", CancellationToken.None);
        failure.StatusCode.Should().Be(400);
        JsonSerializer.Serialize(failure.Value).Should().Contain("No Jira credential");
    }

    [Fact]
    public async Task CommentPage_UsesServerIdentityAndRejectsMissingImageIdentifiers()
    {
        var service = new StubComments();
        var model = Page(service, true);
        model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "reader@example.com"), new Claim(ClaimTypes.Role, TeamHubUserTypes.Privileged)], "Test"));
        await model.OnGetAsync("studio", "SUP-1", CancellationToken.None);
        service.LastQuery!.RequestingUserId.Should().Be("reader@example.com");
        service.LastQuery.AllowPrivilegedDefaultCredential.Should().BeTrue();
        var failure = (JsonResult)await model.OnGetImageAsync("studio", "SUP-1", "", "", CancellationToken.None);
        failure.StatusCode.Should().Be(400);
        service.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CommentPage_SurfacesMalformedResponseWithoutExposingJiraPayload()
    {
        var model = Page(new StubComments { Error = new JsonException("sensitive upstream payload") }, true);
        var failure = (JsonResult)await model.OnGetAsync("studio", "SUP-1", CancellationToken.None);
        failure.StatusCode.Should().Be(502);
        JsonSerializer.Serialize(failure.Value).Should().NotContain("sensitive upstream payload");
    }

    private static SupportSummaryCommentsModel Page(StubComments service, bool allowed) =>
        new(service, new StubAccess(allowed), NullLogger<SupportSummaryCommentsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Png()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aS1sAAAAASUVORK5CYII="))
        };
        response.Content.Headers.ContentType = new("image/png");
        return response;
    }

    private sealed class Host(ServiceProvider provider, AsyncServiceScope scope, string path,
        IStudioJiraCommentService service, StudioJiraCommentQuery query) : IAsyncDisposable
    {
        public IStudioJiraCommentService Service => service;
        public StudioJiraCommentQuery Query => query;
        public async Task ConfigureSharedAccessAsync()
        {
            var atlassian = scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>();
            await atlassian.SaveUserTokensAsync(query.RequestingUserId, null, null, removeJiraToken: true);
            await atlassian.SaveDefaultTokensAsync("shared-test-token", null);
            await atlassian.SavePrivilegedAccessAsync([
                new AtlassianPrivilegedAccess(query.RequestingUserId, true, false)]);
        }
        public async Task DeactivateStudioAsync()
        {
            var directory = scope.ServiceProvider.GetRequiredService<IStudioDirectoryService>();
            var studio = await directory.GetStudioAsync(query.StudioId!);
            studio!.IsActive = false;
            await directory.SaveStudioAsync(studio);
        }
        public async Task ConfigureSharedProjectAsync(string projectKey = "SUP")
        {
            var directory = scope.ServiceProvider.GetRequiredService<IStudioDirectoryService>();
            var second = await directory.SaveStudioAsync(new StudioDetails
            {
                StudioName = "Studio B", ProjectName = "Project", IsActive = true
            });
            await scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>().SaveStudioMappingsAsync([
                new StudioAtlassianMapping { StudioId = query.StudioId!, JiraProjectKeys = [projectKey], JiraStudioComponent = "StudioA" },
                new StudioAtlassianMapping { StudioId = second.Id, JiraProjectKeys = [projectKey], JiraStudioComponent = "StudioB" }]);
        }
        public static async Task<Host> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var path = Path.Combine(Path.GetTempPath(), $"teamhub-jira-comments-{Guid.NewGuid():N}.db");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StudioDb"] = $"Data Source={path}"
            }).Build();
            var services = new ServiceCollection();
            services.AddDataProtection();
            services.AddStudioDirectory(configuration);
            services.AddHttpClient<IStudioJiraCommentService, JiraStudioCommentService>()
                .ConfigurePrimaryHttpMessageHandler(() => new Handler(respond));
            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var studio = await scope.ServiceProvider.GetRequiredService<IStudioDirectoryService>()
                .SaveStudioAsync(new StudioDetails { StudioName = "Studio A", ProjectName = "Project", IsActive = true });
            var atlassian = scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>();
            await atlassian.SaveSettingsAsync(new AtlassianIntegrationSettings
            {
                JiraEnabled = true, JiraBaseUrl = "https://jira.example.test"
            });
            await atlassian.SaveStudioMappingsAsync([
                new StudioAtlassianMapping { StudioId = studio.Id, JiraProjectKeys = ["SUP"], JiraStudioComponent = "StudioA" }]);
            await atlassian.SaveUserTokensAsync("reader@example.com", "personal-test-token", null);
            return new Host(provider, scope, path, scope.ServiceProvider.GetRequiredService<IStudioJiraCommentService>(),
                new StudioJiraCommentQuery(studio.Id, "SUP-1", "reader@example.com", false));
        }
        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
    private sealed class StubComments : IStudioJiraCommentService
    {
        public int Calls { get; private set; }
        public Exception? Error { get; set; }
        public StudioJiraCommentQuery? LastQuery { get; private set; }
        public Task<StudioJiraCommentResult> GetCommentsAsync(StudioJiraCommentQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastQuery = query;
            if (Error is not null) throw Error;
            return Task.FromResult(new StudioJiraCommentResult("SUP-1", "https://jira.example.test/browse/SUP-1", false, []));
        }
        public Task<StudioJiraCommentImage> GetImageAsync(StudioJiraCommentQuery query, string commentId, string attachmentId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Error is not null) throw Error;
            return Task.FromResult(new StudioJiraCommentImage([], "image/png"));
        }
        public Task<StudioJiraMilestoneReviewCommentResult> UpsertMilestoneReviewCommentAsync(
            StudioJiraMilestoneReviewCommentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioJiraMilestoneReviewCommentResult(request.CommentId ?? "1"));
    }
    private sealed class StubAccess(bool allowed) : ICurrentAccessService
    {
        public Task<bool> CanAsync(string module, AccessLevel required = AccessLevel.ReadOnly,
            CancellationToken cancellationToken = default) => Task.FromResult(allowed);
        public Task<AccessLevel> GetAccessLevelAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed ? AccessLevel.ReadOnly : AccessLevel.NoAccess);
        public Task<CurrentAccessSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentAccessSnapshot(TeamHubUserTypes.Registered, null));
    }
}
