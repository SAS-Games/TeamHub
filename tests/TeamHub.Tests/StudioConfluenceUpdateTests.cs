using System.Text.Encodings.Web;
using FluentAssertions;
using TeamHub.Studio;
using TeamHub.Web.Pages.Studio;

namespace TeamHub.Tests;

public sealed class StudioConfluenceUpdateTests
{
    [Fact]
    public void TableParser_ReturnsOnlyRequestedStudioAndMapsWeeklySections()
    {
        const string storage = """
            <table>
              <tbody>
                <tr><th>Studio</th><th>Overview (for WR)</th><th>Notes</th></tr>
                <tr>
                  <td>BBG</td>
                  <td><strong>Studio Work:</strong><br/>Other studio work</td>
                  <td>Other note</td>
                </tr>
                <tr>
                  <td>HDC</td>
                  <td>
                    <p><strong>Studio Work:</strong></p><p>Delivered the gameplay milestone</p>
                    <p><strong>HPGDS Support:</strong></p><p>Investigated build failures</p>
                    <p><strong>WMD Support:</strong></p><p>Reviewed the release checklist</p>
                    <p><strong>Action Item:</strong></p><p>Follow up with production</p>
                  </td>
                  <td>Release is on track</td>
                </tr>
              </tbody>
            </table>
            """;

        var found = StudioConfluenceTableParser.TryParse(storage, "HDC", out var update);

        found.Should().BeTrue();
        update.StudioWork.Should().Be("Delivered the gameplay milestone");
        update.HpgdsSupport.Should().Be("Investigated build failures");
        update.WmdSupport.Should().Be("Reviewed the release checklist");
        update.ActionItems.Should().Be("Follow up with production");
        update.Notes.Should().Be("Release is on track");
    }

    [Fact]
    public void TableParser_PreservesSafeConfluenceLinksAndRendererCreatesAnchors()
    {
        const string body = """
            <table><tbody><tr><td>HDC</td><td>
              <strong>HPGDS Support:</strong><br/>
              Investigated <a href="https://jira.example.test/browse/SIEHP-1153">SIEHP-1153</a>
            </td></tr></tbody></table>
            """;

        StudioConfluenceTableParser.TryParse(body, "HDC", out var update).Should().BeTrue();
        update.HpgdsSupport.Should().Be("Investigated [SIEHP-1153](https://jira.example.test/browse/SIEHP-1153)");

        using var writer = new StringWriter();
        ConfluenceUpdateHtml.Render(update.HpgdsSupport, "No information provided.").WriteTo(writer, HtmlEncoder.Default);
        writer.ToString().Should().Contain("href=\"https://jira.example.test/browse/SIEHP-1153\"");
        writer.ToString().Should().Contain(">SIEHP-1153</a>");
    }

    [Fact]
    public void TableParser_SeparatesEmptyAdjacentSectionsAndPreservesConfluenceLineBreaks()
    {
        const string body = """
            <table><tbody><tr>
              <td>HDC</td>
              <td>
                <strong>Studio Work:</strong>
                Fixing the TRC issues<br data-layout-break="true"/>Implementing activities
                <strong>HPGDS Support:</strong>
                <strong>WMD Support:</strong>Reviewing the milestone
                <strong>Action Item:</strong>
              </td>
              <td>No notes provided.</td>
            </tr></tbody></table>
            """;

        StudioConfluenceTableParser.TryParse(body, "HDC", out var update).Should().BeTrue();

        update.StudioWork.Should().Be(
            $"Fixing the TRC issues{Environment.NewLine}Implementing activities");
        update.HpgdsSupport.Should().BeEmpty();
        update.WmdSupport.Should().Be("Reviewing the milestone");
        update.ActionItems.Should().BeEmpty();
        update.Notes.Should().Be("No notes provided.");
    }

    [Fact]
    public void TableParser_PreservesParagraphListAndConfluenceTaskBoundaries()
    {
        const string body = """
            <table><tbody><tr>
              <td>HDC</td>
              <td>
                <p><strong>Studio Work:</strong></p>
                <p>Level design</p>
                <ul><li>Gameplay fixes</li><li>Animation setup</li></ul>
                <p><strong>HPGDS Support:</strong></p>
                <ac:task-list>
                  <ac:task><ac:task-body>Investigate build</ac:task-body></ac:task>
                </ac:task-list>
                <p><strong>WMD Support:</strong></p>
                <p><strong>Action Items:</strong></p>
              </td>
            </tr></tbody></table>
            """;

        StudioConfluenceTableParser.TryParse(body, "HDC", out var update).Should().BeTrue();

        update.StudioWork.Should().Be(
            string.Join(Environment.NewLine, "Level design", "Gameplay fixes", "Animation setup"));
        update.HpgdsSupport.Should().Be("Investigate build");
        update.WmdSupport.Should().BeEmpty();
        update.ActionItems.Should().BeEmpty();
    }

    [Fact]
    public void PageNaming_UsesBusinessWeekAndConfiguredHierarchyPatterns()
    {
        var selectedDate = new DateOnly(2026, 9, 16);
        var weekStart = StudioConfluencePageNaming.StartOfWeek(selectedDate);
        var weekEnd = weekStart.AddDays(4);

        weekStart.Should().Be(new DateOnly(2026, 9, 14));
        StudioConfluencePageNaming.Format("{Year}", weekStart, weekEnd).Should().Be("2026");
        StudioConfluencePageNaming.Format("{Month}/{Year}", weekStart, weekEnd).Should().Be("9/2026");
        StudioConfluencePageNaming.Format("{WeekStart:dd/MM}-{WeekEnd:dd/MM}", weekStart, weekEnd).Should().Be("14/09-18/09");
    }

    [Fact]
    public void EnumerateWeeks_ReturnsEveryWeeklyPageTouchedByDateRange()
    {
        StudioConfluencePageNaming.EnumerateWeeks(
                new DateOnly(2026, 9, 16),
                new DateOnly(2026, 9, 30))
            .Should()
            .Equal(
                new DateOnly(2026, 9, 14),
                new DateOnly(2026, 9, 21),
                new DateOnly(2026, 9, 28));
    }

    [Fact]
    public void PageNaming_CanUseEitherBoundaryForCrossMonthHierarchy()
    {
        var weekStart = new DateOnly(2026, 8, 31);
        var weekEnd = new DateOnly(2026, 9, 4);

        StudioConfluencePageNaming.Format("{Month:00}/{Year}", weekStart, weekEnd, weekStart)
            .Should().Be("08/2026");
        StudioConfluencePageNaming.Format("{Month:00}/{Year}", weekStart, weekEnd, weekEnd)
            .Should().Be("09/2026");
    }
}
