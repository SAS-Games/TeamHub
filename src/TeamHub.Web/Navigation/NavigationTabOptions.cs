namespace TeamHub.Web.Navigation;

public sealed class NavigationTabOptions
{
    public string Name { get; set; } = string.Empty;
    public string Page { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool AdminOnly { get; set; }
}