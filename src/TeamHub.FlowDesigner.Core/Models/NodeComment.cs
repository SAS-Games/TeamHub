namespace TeamHub.FlowDesigner.Core.Models;

public sealed class NodeComment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Body { get; set; } = string.Empty;
    public string Author { get; set; } = "Unknown user";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
