using TeamHub.Team;

namespace TeamHub.Web.Layout;

public sealed record PageLayoutDefinition(
    string PageKey,
    string Name,
    string Category,
    string DefaultWidth = CustomTeamPageWidths.Standard);

public static class PageLayoutCatalog
{
    public static IReadOnlyList<PageLayoutDefinition> Pages { get; } =
    [
        new("/Index", "Home", "General"),
        new("/Notifications/Index", "Notifications", "General"),
        new("/Profile/Atlassian", "Atlassian profile", "General"),
        new("/Privacy", "Privacy", "General"),

        new("/WorkCenter/Overview", "Work Center overview", "Work Center"),
        new("/WorkCenter/MyTasks", "My tasks", "Work Center"),
        new("/WorkCenter/StartNew", "Start workflow", "Work Center"),
        new("/Workflows/Details", "Workflow details", "Work Center"),
        new("/WorkCenter/Configuration", "Workflow configuration", "Work Center"),

        new("/Team/Directory", "Team directory", "Team"),
        new("/Team/Achievements", "Achievements", "Team"),
        new("/Team/ResponsibilityMatrix", "Responsibility matrix", "Team"),
        new("/Team/Configuration", "Team configuration", "Team"),

        new("/Milestones", "Milestone tracker", "Milestones"),
        new("/MilestoneReview", "Milestone review", "Milestones"),

        new("/Studio/Index", "Studios", "Studios"),
        new("/Studio/WeeklyUpdates", "Weekly updates", "Studios"),
        new("/Studio/JiraTickets", "Jira tickets", "Studios"),
        new("/Studio/Configuration", "Studio configuration", "Studios"),

        new("/SupportSummary", "Support summary", "Support", CustomTeamPageWidths.Full),
        new("/SupportSummaryComments", "Support summary comments", "Support"),

        new("/Flows/Index", "Flow Designer", "Flow Designer"),
        new("/Flows/New", "New flow", "Flow Designer"),
        new("/Flows/CreateWithAi", "Create flow with AI", "Flow Designer"),
        new("/Flows/View", "Published flow", "Flow Designer"),
        new("/Flows/Review", "Flow review", "Flow Designer"),

        new("/Configuration/Index", "Configuration home", "Administration"),
        new("/Configuration/AI", "AI configuration", "Administration"),
        new("/Configuration/Email", "Email configuration", "Administration"),
        new("/Configuration/Atlassian", "Jira & Confluence configuration", "Administration"),
        new("/Configuration/Milestones", "Milestone configuration", "Administration"),
        new("/Configuration/Templates", "Template catalog", "Administration"),
        new("/Configuration/PageLayouts", "Page layout configuration", "Administration"),
        new("/Administration/Users", "Authorized users", "Administration"),
        new("/Administration/Access", "Access management", "Administration"),

        new("/Login", "Sign in", "Account"),
        new("/Register", "Register", "Account"),
        new("/AcceptInvitation", "Accept invitation", "Account"),
        new("/ForgotPassword", "Forgot password", "Account"),
        new("/ResetPassword", "Reset password", "Account"),
        new("/AccessDenied", "Access denied", "Account")
    ];

    public static PageLayoutDefinition? Find(string? pageKey) => Pages.FirstOrDefault(page =>
        string.Equals(page.PageKey, pageKey, StringComparison.OrdinalIgnoreCase));
}
