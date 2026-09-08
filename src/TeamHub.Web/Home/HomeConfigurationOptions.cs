namespace TeamHub.Web.Home;

public sealed class HomeConfigurationOptions
{
    public const string SectionName = "HomeConfiguration";
    public string ProjectInfoPath { get; set; } = "config/Home/project-info.json";
    public string UsefulLinksPath { get; set; } = "config/Home/useful-links.json";
}
