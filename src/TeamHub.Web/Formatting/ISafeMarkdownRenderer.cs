namespace TeamHub.Web.Formatting;

public interface ISafeMarkdownRenderer
{
    string ToHtml(string? markdown);
}
