using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;

namespace TeamHub.FlowDesigner.Validation;

public sealed class FlowValidator : IFlowValidator
{
    public FlowValidationResult Validate(FlowDefinition flow)
    {
        var issues = new List<FlowValidationIssue>();
        var nodeGroups = flow.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal);

        foreach (var group in nodeGroups.Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
        {
            issues.Add(new(
                "duplicate-node-id",
                string.IsNullOrWhiteSpace(group.Key) ? "Every node must have an ID." : $"Node ID '{group.Key}' is duplicated.",
                ValidationSeverity.Error,
                group.Key));
        }

        var nodeIds = flow.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Id))
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in flow.Connections.GroupBy(connection => connection.Id, StringComparer.Ordinal)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
        {
            issues.Add(new(
                "duplicate-connection-id",
                string.IsNullOrWhiteSpace(duplicate.Key) ? "Every connection must have an ID." : $"Connection ID '{duplicate.Key}' is duplicated.",
                ValidationSeverity.Error,
                duplicate.Key));
        }

        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in flow.Connections)
        {
            if (!nodeIds.Contains(connection.SourceNodeId))
            {
                issues.Add(new("missing-source", $"Connection '{connection.Id}' has a missing source node.", ValidationSeverity.Error, connection.Id));
            }

            if (!nodeIds.Contains(connection.TargetNodeId))
            {
                issues.Add(new("missing-target", $"Connection '{connection.Id}' has a missing target node.", ValidationSeverity.Error, connection.Id));
            }

            if (connection.SourceNodeId == connection.TargetNodeId)
            {
                issues.Add(new("self-connection", "A node cannot connect to itself.", ValidationSeverity.Error, connection.Id));
            }

            var edgeKey = $"{connection.SourceNodeId}\u001f{connection.SourcePort}\u001f{connection.TargetNodeId}\u001f{connection.TargetPort}";
            if (!edgeKeys.Add(edgeKey))
            {
                issues.Add(new("duplicate-connection", "The same ports are connected more than once.", ValidationSeverity.Error, connection.Id));
            }
        }

        var starts = flow.Nodes.Count(node => node.Type == NodeType.Start);
        if (flow.DiagramType == DiagramType.BusinessWorkflow && starts == 0)
        {
            issues.Add(new("missing-start", "At least one Start event is recommended.", ValidationSeverity.Warning));
        }
        else if (flow.DiagramType != DiagramType.BusinessWorkflow && starts != 1)
        {
            issues.Add(new("start-count", $"Exactly one Start node is recommended; this flow has {starts}.", ValidationSeverity.Warning));
        }

        if (flow.Nodes.All(node => node.Type != NodeType.End))
        {
            issues.Add(new("missing-end", "At least one End node is recommended.", ValidationSeverity.Warning));
        }

        var connectedIds = flow.Connections
            .SelectMany(connection => new[] { connection.SourceNodeId, connection.TargetNodeId })
            .ToHashSet(StringComparer.Ordinal);
        foreach (var orphan in flow.Nodes.Where(node => node.Type != NodeType.Note && !connectedIds.Contains(node.Id)))
        {
            issues.Add(new("orphan-node", $"'{orphan.Title}' is not connected.", ValidationSeverity.Warning, orphan.Id));
        }

        var branchingTypes = new[] { NodeType.Decision, NodeType.Gateway, NodeType.ParallelGateway };
        foreach (var branch in flow.Nodes.Where(node => branchingTypes.Contains(node.Type)))
        {
            var outgoingCount = flow.Connections.Count(connection => connection.SourceNodeId == branch.Id);
            if (outgoingCount < 2)
            {
                issues.Add(new("incomplete-branch", $"'{branch.Title}' should have at least two outgoing paths.", ValidationSeverity.Warning, branch.Id));
            }
        }

        return new FlowValidationResult { Issues = issues };
    }
}
