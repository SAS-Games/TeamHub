using FluentAssertions;
using TeamHub.AI.Chat.Contracts;
using TeamHub.Web.AI;

namespace TeamHub.Tests;

public sealed class TeamHubAiInstructionFileProviderTests
{
    private const string Playbook = """
        # Runtime playbook

        ## Core
        core-rules

        ## Team directory
        directory-rules

        ## Milestone Tracker
        milestone-rules

        ## Support Summary
        support-rules

        ## Jira enrichment
        jira-rules

        ## Combining sources
        combination-rules

        ## Dates and current state
        date-rules

        ## Reports and tables
        report-rules

        ## Charts and diagrams
        chart-rules
        """;

    [Fact]
    public void Source_free_ordinary_question_sends_no_playbook_text()
    {
        var result = TeamHubAiInstructionFileProvider.SelectSections(
            Playbook,
            "What is 2 + 2?",
            []);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Directory_question_sends_only_core_and_directory_sections()
    {
        AiChatSource[] sources = [new("teamhub-team-directory", "Directory", "Ramu")];

        var result = TeamHubAiInstructionFileProvider.SelectSections(
            Playbook,
            "Who is Ramu?",
            sources);

        result.Should().Contain("core-rules")
            .And.Contain("directory-rules")
            .And.NotContain("milestone-rules")
            .And.NotContain("support-rules")
            .And.NotContain("chart-rules");
    }

    [Fact]
    public void Words_containing_a_routing_term_do_not_add_an_unrelated_section()
    {
        AiChatSource[] sources = [new("teamhub-team-directory", "Directory", "Ramu")];

        var result = TeamHubAiInstructionFileProvider.SelectSections(
            Playbook,
            "Give me information about Ramu",
            sources);

        result.Should().NotContain("report-rules");
    }

    [Fact]
    public void Support_chart_question_adds_only_relevant_cross_source_modules()
    {
        AiChatSource[] sources =
        [
            new("teamhub-support-active-milestones", "Support", "Active milestone"),
            new("teamhub-support-jira", "Jira", "Ticket")
        ];

        var result = TeamHubAiInstructionFileProvider.SelectSections(
            Playbook,
            "Create a chart of current support activity",
            sources);

        result.Should().Contain("core-rules")
            .And.Contain("support-rules")
            .And.Contain("jira-rules")
            .And.Contain("combination-rules")
            .And.Contain("date-rules")
            .And.Contain("chart-rules")
            .And.NotContain("directory-rules")
            .And.NotContain("report-rules");
    }
}
