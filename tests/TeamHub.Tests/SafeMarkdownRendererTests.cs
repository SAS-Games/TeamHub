using TeamHub.Web.Formatting;

namespace TeamHub.Tests;

public sealed class SafeMarkdownRendererTests
{
    private readonly SafeMarkdownRenderer _renderer = new();

    [Fact]
    public void RendersSupportedBlockAndInlineFormatting()
    {
        var markdown = "## Update\n\n"
            + "**Done**, *reviewed*, ==important==, ~~old~~ and \u0060git pull\u0060.\n\n"
            + "- First\n- Second\n\n"
            + "1. Build\n2. Test\n\n"
            + "> Keep this visible";

        var html = _renderer.ToHtml(markdown);

        Assert.Contains("<h2>Update</h2>", html);
        Assert.Contains("<strong>Done</strong>", html);
        Assert.Contains("<em>reviewed</em>", html);
        Assert.Contains("<mark>important</mark>", html);
        Assert.Contains("<del>old</del>", html);
        Assert.Contains("<code>git pull</code>", html);
        Assert.Contains("<ul><li>First</li><li>Second</li></ul>", html);
        Assert.Contains("<ol><li>Build</li><li>Test</li></ol>", html);
        Assert.Contains("<blockquote>Keep this visible</blockquote>", html);
    }

    [Fact]
    public void PreservesLineBreaksInsideAParagraph()
    {
        var html = _renderer.ToHtml("First line\nSecond line");

        Assert.Equal("<p>First line<br />Second line</p>", html);
    }

    [Fact]
    public void RendersCombinedBoldAndItalicWithoutLeavingMarkers()
    {
        var html = _renderer.ToHtml("***Release ready***");

        Assert.Equal("<p><strong><em>Release ready</em></strong></p>", html);
    }

    [Fact]
    public void RendersNestedLists()
    {
        var html = _renderer.ToHtml("- Submitted DevDay build.\n"
            + "  - They have defaulted to Pre-Alpha build submission.\n"
            + "- Studio requested PS5 testing.\n"
            + "  - PS5 access is limited.\n"
            + "  - They need help spotting issues.");

        Assert.Equal("<ul><li>Submitted DevDay build.<ul><li>They have defaulted to Pre-Alpha build submission.</li></ul></li><li>Studio requested PS5 testing.<ul><li>PS5 access is limited.</li><li>They need help spotting issues.</li></ul></li></ul>", html);
    }

    [Fact]
    public void RendersOnlySafeLinksAndEncodesRawHtml()
    {
        var markdown = "[Jira](https://jira.example.com/browse/TEAM-42)\n"
            + "[Unsafe](javascript:alert(1))\n"
            + "<script>alert('xss')</script>";

        var html = _renderer.ToHtml(markdown);

        Assert.Contains("href='https://jira.example.com/browse/TEAM-42'", html);
        Assert.DoesNotContain("href='javascript:", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
