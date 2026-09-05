namespace TeamHub.Web.Home;

public sealed class HomeConfigurationOptions
{
    public const string SectionName = "HomeConfiguration";
    public string ProjectInfoPath { get; set; } = string.Empty;
    public string UsefulLinksPath { get; set; } = string.Empty;
}
