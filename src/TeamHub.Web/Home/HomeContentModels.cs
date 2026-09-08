namespace TeamHub.Web.Home;

public sealed class HomeContent
{
    public string ProjectName { get; set; } = string.Empty;
    public string Eyebrow { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string BackgroundImage { get; set; } = string.Empty;
    public string Vision { get; set; } = string.Empty;
    public string Mission { get; set; } = string.Empty;
    public string VisionMissionKicker { get; set; } = "Why we exist";
    public string VisionMissionTitle { get; set; } = "Vision & Mission";
    public string VisionLabel { get; set; } = "Vision";
    public string MissionLabel { get; set; } = "Mission";
    public string AboutKicker { get; set; } = "Overview";
    public string AboutTitle { get; set; } = "About This Project";
    public string Description { get; set; } = string.Empty;
    public string HighlightsKicker { get; set; } = "What's inside";
    public string HighlightsTitle { get; set; } = "Highlights";
    public List<HomeHighlight> Highlights { get; set; } = [];
    public string UsefulLinksKicker { get; set; } = "Quick access";
    public string UsefulLinksTitle { get; set; } = "Useful Links";
    public List<UsefulLink> UsefulLinks { get; set; } = [];
}

public sealed class HomeHighlight
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class UsefulLink
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
