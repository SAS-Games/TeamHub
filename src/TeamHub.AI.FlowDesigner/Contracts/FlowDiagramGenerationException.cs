namespace TeamHub.AI.FlowDesigner.Contracts;

public sealed class FlowDiagramGenerationException : Exception
{
    public FlowDiagramGenerationException(string message) : base(message)
    {
    }

    public FlowDiagramGenerationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}